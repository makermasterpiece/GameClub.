using GameClub.Domain.Commands;
using GameClub.Domain.Stations;
using GameClub.Infrastructure.Persistence;
using GameClub.Domain.Security;
using GameClub.Server.Security;
using Microsoft.EntityFrameworkCore;
using GameClub.Application.Abstractions;
using GameClub.Domain.Gaming;
using GameClub.Domain.Users;
using System.Data;
using System.Text.Json;
using GameClub.Application.Games;
using GameClub.Contracts.Games;

namespace GameClub.Server.Services.Commands;

public sealed class AgentCommandService(
    GameClubDbContext dbContext,
    IStationCommandTransport transport,
    IServerCommandSigner commandSigner,
    TimeProvider timeProvider,
    ILogger<AgentCommandService> logger,
    GameLaunchService? gameLaunch = null) : IAgentCommandService
{
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(5);
    public const int MaximumErrorLength = 2000;

    public async Task<AgentCommand?> CreateCommandAsync(
        Guid stationId,
        AgentCommandType type,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported command type.");
        }

        if (payloadJson is not null &&
            System.Text.Encoding.UTF8.GetByteCount(payloadJson) > CommandEnvelopeCryptography.MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadJson), "Payload exceeds 16 KiB.");
        }

        var stationExists = await dbContext.Stations
            .AnyAsync(station => station.Id == stationId, cancellationToken);
        if (!stationExists)
        {
            return null;
        }

        var now = GetUtcNow();
        if (type == AgentCommandType.LaunchGame)
        {
            using var document = JsonDocument.Parse(payloadJson ?? "null");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("gameId", out var value) || value.ValueKind != JsonValueKind.String ||
                !value.TryGetGuid(out var gameId) || gameId == Guid.Empty)
                throw new ArgumentException("LaunchGame accepts only a non-empty gameId.", nameof(payloadJson));
            var sessionId = await dbContext.Set<GamingSession>().Where(s => s.StationId == stationId &&
                s.Status == GamingSessionStatus.Active).Select(s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ClubException("ACTIVE_PAID_SESSION_REQUIRED");
            var grant = await RequireGameLaunch().ValidateCoreAsync(stationId, sessionId, gameId, cancellationToken);
            payloadJson = JsonSerializer.Serialize(new LaunchGamePayload(gameId, sessionId, grant.PlayniteGameId!.Value),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (StationPowerPolicy.IsPowerType(type))
        {
            if (payloadJson is not null) throw new ArgumentException("Power commands do not accept a payload.", nameof(payloadJson));
            var rejection = await GetPowerRejectionAsync(stationId, null, now, cancellationToken);
            if (rejection is not null) throw new ClubException(rejection);
        }
        var command = new AgentCommand(
            Guid.NewGuid(),
            stationId,
            type,
            payloadJson,
            now,
            now + (StationPowerPolicy.IsPowerType(type) ? StationPowerPolicy.CommandLifetime :
                type == AgentCommandType.LaunchGame ? GameLaunchService.CommandLifetime : CommandLifetime));

        dbContext.AgentCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Command {CommandId} created for station {StationId}. Type: {CommandType}",
            command.Id,
            stationId,
            type);

        return command;
    }

    public async Task<AgentCommand?> DispatchCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var command = await dbContext.AgentCommands
            .Include(candidate => candidate.Station)
            .SingleOrDefaultAsync(candidate => candidate.Id == commandId, cancellationToken);

        if (command is null)
        {
            return null;
        }

        if (StationPowerPolicy.IsPowerType(command.Type))
            return await DispatchPowerAsync(command, cancellationToken);

        if (command.Type == AgentCommandType.LaunchGame &&
            command.Status is AgentCommandStatus.Pending or AgentCommandStatus.Sent &&
            !await ValidateGameCommandAsync(command, cancellationToken)) return command;

        var now = GetUtcNow();
        if (command.MarkExpired(now))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Command {CommandId} expired", command.Id);
            return command;
        }

        if (command.Status is not (AgentCommandStatus.Pending or AgentCommandStatus.Sent) ||
            command.Station.Status == StationStatus.Offline ||
            !await dbContext.StationCredentials.AsNoTracking().AnyAsync(
                credential => credential.StationId == command.StationId &&
                              credential.RevokedAtUtc == null,
                cancellationToken) ||
            !transport.IsConnected(command.StationId))
        {
            return command;
        }

        command.MarkSent(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await transport.SendAsync(
                command.StationId,
                commandSigner.CreateSignedEnvelope(command),
                cancellationToken);

            logger.LogInformation(
                "Command {CommandId} sent to station {StationId}",
                command.Id,
                command.StationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not send command {CommandId} to station {StationId}",
                command.Id,
                command.StationId);

            await dbContext.Entry(command).ReloadAsync(cancellationToken);
            if (command.ReturnToPendingAfterDispatchFailure())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return command;
        }

        await dbContext.Entry(command).ReloadAsync(cancellationToken);
        return command;
    }

    public async Task DispatchPendingCommandsAsync(
        Guid stationId,
        CancellationToken cancellationToken)
    {
        var commandIds = await dbContext.AgentCommands
            .AsNoTracking()
            .Where(command =>
                command.StationId == stationId &&
                (command.Status == AgentCommandStatus.Pending ||
                 command.Status == AgentCommandStatus.Sent))
            .OrderBy(command => command.CreatedAtUtc)
            .Select(command => command.Id)
            .ToListAsync(cancellationToken);

        foreach (var commandId in commandIds)
        {
            await DispatchCommandAsync(commandId, cancellationToken);
        }
    }

    public Task<CommandOperationResult> AcknowledgeAsync(
        Guid commandId,
        Guid authenticatedStationId,
        CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            commandId,
            authenticatedStationId,
            "acknowledged",
            command => StationPowerPolicy.IsPowerType(command.Type) && command.Status == AgentCommandStatus.Acknowledged ||
                       command.Acknowledge(GetUtcNow()),
            cancellationToken);

    public Task<CommandOperationResult> CompleteAsync(
        Guid commandId,
        Guid authenticatedStationId,
        CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            commandId,
            authenticatedStationId,
            "completed",
            command => command.Complete(GetUtcNow()),
            cancellationToken);

    public Task<CommandOperationResult> FailAsync(
        Guid commandId,
        Guid authenticatedStationId,
        string? error,
        CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            commandId,
            authenticatedStationId,
            "failed",
            command => command.Fail(GetUtcNow(), NormalizeError(error)),
            cancellationToken);

    private async Task<CommandOperationResult> ChangeStatusAsync(
        Guid commandId,
        Guid authenticatedStationId,
        string action,
        Func<AgentCommand, bool> transition,
        CancellationToken cancellationToken)
    {
        var command = await dbContext.AgentCommands
            .SingleOrDefaultAsync(candidate => candidate.Id == commandId, cancellationToken);

        if (command is null)
        {
            return CommandOperationResult.NotFound;
        }

        if (command.StationId != authenticatedStationId)
        {
            logger.LogWarning(
                "Security rejection: station {StationId} attempted to mark command {CommandId} owned by another station as {CommandAction}",
                authenticatedStationId,
                commandId,
                action);
            return CommandOperationResult.StationMismatch;
        }

        var previousStatus = command.Status;
        if (action == "acknowledged" && command.Type == AgentCommandType.LaunchGame &&
            !await ValidateGameCommandAsync(command, cancellationToken)) return CommandOperationResult.InvalidState;
        if (!transition(command))
        {
            if (command.Status != previousStatus)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Command {CommandId} expired", command.Id);
            }

            return CommandOperationResult.InvalidState;
        }

        if (command.Status != previousStatus)
        {
            if (StationPowerPolicy.IsPowerType(command.Type))
                dbContext.SecurityAuditEvents.Add(new SecurityAuditEvent(Guid.NewGuid(),
                    command.Status == AgentCommandStatus.Completed ? "StationPowerRequestAccepted" : "StationPowerCommandStatusChanged",
                    command.StationId, GetUtcNow(), null,
                    $"CommandId={command.Id:D};Type={command.Type};Status={command.Status}"));
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Command {CommandId} {CommandAction} by station {StationId}",
                command.Id,
                action,
                authenticatedStationId);
        }

        return CommandOperationResult.Success;
    }

    private DateTime GetUtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private GameLaunchService RequireGameLaunch() => gameLaunch ??
        throw new ClubException("GAME_LAUNCH_UNAVAILABLE");

    private async Task<bool> ValidateGameCommandAsync(AgentCommand command, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<LaunchGamePayload>(command.PayloadJson ?? "null",
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ClubException("INVALID_GAME_COMMAND");
            var grant = await RequireGameLaunch().ValidateCoreAsync(command.StationId, payload.GamingSessionId, payload.GameId, ct);
            if (grant.PlayniteGameId != payload.PlayniteGameId) throw new ClubException("PLAYNITE_GAME_MISMATCH");
            return true;
        }
        catch (ClubException ex)
        {
            command.RejectGameLaunch(GetUtcNow(), ex.Code);
            dbContext.SecurityAuditEvents.Add(new SecurityAuditEvent(Guid.NewGuid(), "GameLaunchCommandRejected",
                command.StationId, GetUtcNow(), null, $"CommandId={command.Id:D};Reason={ex.Code}"));
            await dbContext.SaveChangesAsync(ct);
            return false;
        }
    }

    private async Task<string?> GetPowerRejectionAsync(Guid stationId, Guid? ownCommandId, DateTime now, CancellationToken ct)
    {
        var station = await dbContext.Stations.AsNoTracking().SingleOrDefaultAsync(s => s.Id == stationId, ct);
        if (station is null) return "STATION_NOT_FOUND";
        if (station.Status != StationStatus.Online || station.LastSeenAtUtc > now ||
            now - station.LastSeenAtUtc > StationPowerPolicy.HeartbeatFreshness)
            return "STATION_OFFLINE";
        if (await dbContext.Set<PlayerAuthSession>().AsNoTracking().AnyAsync(
                s => s.StationId == stationId && s.Status == PlayerAuthSessionStatus.Active, ct) ||
            await dbContext.Set<GamingSession>().AsNoTracking().AnyAsync(s => s.StationId == stationId &&
                (s.Status == GamingSessionStatus.Created || s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused), ct))
            return "STATION_BUSY";
        var leaseCutoff = now - StationPowerPolicy.BusyGrace;
        if (await dbContext.AgentCommands.AsNoTracking().AnyAsync(c => c.StationId == stationId && c.Id != ownCommandId &&
                (c.Type == AgentCommandType.RestartStation || c.Type == AgentCommandType.ShutdownStation) &&
                c.Status != AgentCommandStatus.Failed && c.Status != AgentCommandStatus.Expired && c.ExpiresAtUtc > leaseCutoff, ct))
            return "STATION_POWER_PENDING";
        return null;
    }

    private async Task<AgentCommand> DispatchPowerAsync(AgentCommand command, CancellationToken ct)
    {
        // Read/status transition and busy checks share a serializable snapshot; notification follows commit.
        await using (var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null)
        {
            await dbContext.Entry(command).ReloadAsync(ct);
            if (command.Status is not (AgentCommandStatus.Pending or AgentCommandStatus.Sent)) return command;
            var now = GetUtcNow();
            if (command.MarkExpired(now))
            {
                await dbContext.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return command;
            }
            var rejection = await GetPowerRejectionAsync(command.StationId, command.Id, now, ct);
            if (rejection is not null)
            {
                command.RejectPowerDispatch(now, rejection);
                dbContext.SecurityAuditEvents.Add(new SecurityAuditEvent(Guid.NewGuid(), "StationPowerDispatchRejected",
                    command.StationId, now, null, $"CommandId={command.Id:D};Type={command.Type};Reason={rejection}"));
                await dbContext.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return command;
            }
            if (!transport.IsConnected(command.StationId) ||
                !await dbContext.StationCredentials.AsNoTracking().AnyAsync(c => c.StationId == command.StationId && c.RevokedAtUtc == null, ct))
                return command;
            command.MarkSent(now);
            await dbContext.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        try
        {
            await transport.SendAsync(command.StationId, commandSigner.CreateSignedEnvelope(command), ct);
            logger.LogInformation("Power command {CommandId} sent to station {StationId}", command.Id, command.StationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Delivery may have succeeded. Keep Sent and the lease; a signed duplicate can report its persisted result.
            logger.LogWarning("Power command {CommandId} delivery is uncertain ({ErrorType})", command.Id, exception.GetType().Name);
        }
        await dbContext.Entry(command).ReloadAsync(ct);
        return command;
    }

    private static string NormalizeError(string? error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "Agent reported an unspecified error." : error.Trim();
        return value.Length <= MaximumErrorLength ? value : value[..MaximumErrorLength];
    }
}
