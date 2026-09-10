using System.Text.Json;
using GameClub.Domain.Commands;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Commands;
using GameClub.Server.Services.Commands;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameClub.Domain.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using GameClub.Server.Security;
using GameClub.Server.Security.Employees;
using System.Data;
using Npgsql;
using GameClub.Application.Abstractions;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/stations/{stationId:guid}/commands")]
public sealed class StationCommandsController(
    GameClubDbContext dbContext,
    IAgentCommandService commandService,
    ISecurityAuditService audit,
    IAuthorizationService authorization) : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 200;

    [HttpPost]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    [EnableRateLimiting("station-commands")]
    [RequestSizeLimit(20 * 1024)]
    [ProducesResponseType<CreateAgentCommandResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreateAgentCommandResponse>> Create(
        Guid stationId,
        CreateAgentCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AgentCommandType>(request.Type, ignoreCase: true, out var commandType) ||
            !Enum.IsDefined(commandType) ||
            !string.Equals(request.Type, commandType.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                nameof(request.Type),
                "Supported command types are Ping, TestMessage, LockStation, UnlockStation, LogoutPlayer, RestartStation, ShutdownStation and LaunchGame.");
            return ValidationProblem(ModelState);
        }

        var powerCommand = StationPowerPolicy.IsPowerType(commandType);
        if (powerCommand && !(await authorization.AuthorizeAsync(User, null, EmployeePermissions.Power)).Succeeded)
        {
            await audit.WriteAsync("EmployeeStationPowerDenied", stationId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                $"Type={commandType};EmployeeId={Guid.Parse(User.FindFirst("employee_id")!.Value):D}", cancellationToken);
            return Forbid(EmployeeAuthenticationDefaults.Scheme);
        }

        var payloadJson = SerializePayload(request.Payload);
        if (payloadJson is not null &&
            System.Text.Encoding.UTF8.GetByteCount(payloadJson) > CommandEnvelopeCryptography.MaximumPayloadBytes)
        {
            ModelState.AddModelError(nameof(request.Payload), "Payload must not exceed 16 KiB.");
            return ValidationProblem(ModelState);
        }

        if (commandType is (AgentCommandType.Ping or
            AgentCommandType.LockStation or
            AgentCommandType.UnlockStation or
            AgentCommandType.LogoutPlayer or
            AgentCommandType.RestartStation or
            AgentCommandType.ShutdownStation) && payloadJson is not null)
        {
            ModelState.AddModelError(nameof(request.Payload), $"{commandType} does not accept a payload.");
            return ValidationProblem(ModelState);
        }

        if (commandType == AgentCommandType.TestMessage &&
            !TryValidateTestMessage(request.Payload, out var payloadError))
        {
            ModelState.AddModelError(nameof(request.Payload), payloadError);
            return ValidationProblem(ModelState);
        }

        if (commandType == AgentCommandType.LaunchGame &&
            (request.Payload is not { ValueKind: JsonValueKind.Object } gamePayload ||
             gamePayload.EnumerateObject().Count() != 1 || !gamePayload.TryGetProperty("gameId", out var gameId) ||
             gameId.ValueKind != JsonValueKind.String || !gameId.TryGetGuid(out var parsedGameId) || parsedGameId == Guid.Empty))
        {
            ModelState.AddModelError(nameof(request.Payload), "LaunchGame accepts only a non-empty gameId.");
            return ValidationProblem(ModelState);
        }

        AgentCommand command;
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                powerCommand || commandType == AgentCommandType.LaunchGame ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, cancellationToken);
            var created = await commandService.CreateCommandAsync(stationId, commandType, payloadJson, cancellationToken);
            if (created is null) return NotFound();
            command = created;
            await audit.WriteAsync(powerCommand ? "EmployeeStationPowerRequested" : "EmployeeStationCommandCreated", stationId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                $"CommandId={command.Id:D};Type={commandType};EmployeeId={Guid.Parse(User.FindFirst("employee_id")!.Value):D}",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ClubException exception) when (powerCommand &&
            exception.Code is "STATION_BUSY" or "STATION_OFFLINE" or "STATION_POWER_PENDING")
        {
            // The failed create transaction is disposed before this catch. Never re-save its tracked mutations.
            await AuditRejectedPowerAsync(stationId, commandType, exception.Code, cancellationToken);
            return Conflict(new { code = exception.Code });
        }
        catch (ClubException exception) when (commandType == AgentCommandType.LaunchGame)
        {
            dbContext.ChangeTracker.Clear();
            await audit.WriteAsync("EmployeeGameLaunchDenied", stationId, HttpContext.Connection.RemoteIpAddress?.ToString(),
                $"Reason={exception.Code};EmployeeId={Guid.Parse(User.FindFirst("employee_id")!.Value):D}", cancellationToken);
            return Conflict(new { code = exception.Code });
        }
        catch (Exception exception) when (IsSerializationConflict(exception))
        {
            if (powerCommand)
                await AuditRejectedPowerAsync(stationId, commandType, "CONCURRENT_CONFLICT", cancellationToken);
            else
                dbContext.ChangeTracker.Clear();
            return Conflict(new { code = "CONCURRENT_CONFLICT" });
        }

        command = await commandService.DispatchCommandAsync(command.Id, cancellationToken)
            ?? throw new InvalidOperationException("Created command could not be reloaded.");

        return CreatedAtRoute(
            CommandsController.RouteName,
            new { commandId = command.Id },
            new CreateAgentCommandResponse(
                command.Id,
                command.StationId,
                command.Type,
                command.Status,
                command.CreatedAtUtc,
                command.SentAtUtc));
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    [ProducesResponseType<IReadOnlyList<AgentCommandResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<AgentCommandResponse>>> GetLatest(
        Guid stationId,
        [FromQuery] int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            ModelState.AddModelError(nameof(limit), $"Limit must be between 1 and {MaximumLimit}.");
            return ValidationProblem(ModelState);
        }

        var stationExists = await dbContext.Stations
            .AsNoTracking()
            .AnyAsync(station => station.Id == stationId, cancellationToken);
        if (!stationExists)
        {
            return NotFound();
        }

        var commands = await dbContext.AgentCommands
            .AsNoTracking()
            .Where(command => command.StationId == stationId)
            .OrderByDescending(command => command.CreatedAtUtc)
            .Take(limit)
            .Select(command => new AgentCommandResponse(
                command.Id,
                command.StationId,
                command.Type,
                command.Status,
                command.CreatedAtUtc,
                command.ExpiresAtUtc,
                command.SentAtUtc,
                command.AcknowledgedAtUtc,
                command.CompletedAtUtc,
                command.FailedAtUtc,
                command.ErrorMessage))
            .ToListAsync(cancellationToken);

        return Ok(commands);
    }

    private static string? SerializePayload(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return payload.Value.GetRawText();
    }

    private async Task AuditRejectedPowerAsync(Guid stationId, AgentCommandType type, string reason, CancellationToken ct)
    {
        dbContext.ChangeTracker.Clear();
        await audit.WriteAsync("EmployeeStationPowerRejected", stationId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"Type={type};Reason={reason};EmployeeId={Guid.Parse(User.FindFirst("employee_id")!.Value):D}", ct);
    }

    private static bool IsSerializationConflict(Exception exception)
    {
        // Npgsql's execution strategy can wrap DbUpdateException inside InvalidOperationException.
        // Bound traversal and classify only PostgreSQL transaction conflicts, never arbitrary application errors.
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
            if (current is PostgresException
                { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
        return false;
    }

    private static bool TryValidateTestMessage(JsonElement? payload, out string error)
    {
        error = "TestMessage payload must contain only a non-empty message up to 1000 characters.";
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var properties = payload.Value.EnumerateObject().ToArray();
        var message = messageElement.GetString();
        return properties.Length == 1 && properties[0].NameEquals("message") &&
               !string.IsNullOrWhiteSpace(message) &&
               message.Length <= CommandEnvelopeCryptography.MaximumTestMessageLength;
    }
}
