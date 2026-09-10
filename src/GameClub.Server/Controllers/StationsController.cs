using GameClub.Domain.Stations;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Stations;
using GameClub.Server.Hubs;
using GameClub.Server.Security;
using GameClub.Server.Services.Commands;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using GameClub.Server.Contracts.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using GameClub.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using GameClub.Server.Security.Employees;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/stations")]
public sealed class StationsController(
    GameClubDbContext dbContext,
    IHubContext<StationsHub> hubContext,
    IStationTokenService tokenService,
    IStationEnrollmentService enrollmentService,
    IStationHmacAuthenticator hmacAuthenticator,
    ISecurityAuditService audit,
    IAgentCommandService commandService,
    IWebHostEnvironment environment,
    IOptions<SecurityOptions> securityOptions,
    ILogger<StationsController> logger) : ControllerBase
{
    private const string LegacyStationTokenHeader = "X-Station-Token";

    [HttpGet]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    [ProducesResponseType<IReadOnlyList<StationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StationResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var stations = await dbContext.Stations
            .AsNoTracking()
            .OrderBy(station => station.Name)
            .Select(station => new StationResponse(
                station.Id,
                station.Name,
                station.MachineName,
                station.IpAddress,
                station.AgentVersion,
                station.Status,
                station.CreatedAtUtc,
                station.LastSeenAtUtc,
                station.PlayerAuthSessions
                    .Where(session => session.Status == PlayerAuthSessionStatus.Active &&
                                      session.ExpiresAtUtc > now)
                    .Select(session => (Guid?)session.UserId)
                    .FirstOrDefault(),
                station.PlayerAuthSessions
                    .Where(session => session.Status == PlayerAuthSessionStatus.Active &&
                                      session.ExpiresAtUtc > now)
                    .Select(session => session.User.Username)
                    .FirstOrDefault(),
                station.PlayerAuthSessions
                    .Where(session => session.Status == PlayerAuthSessionStatus.Active &&
                                      session.ExpiresAtUtc > now)
                    .Select(session => (Guid?)session.Id)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return Ok(stations);
    }

    [HttpGet("{id:guid}")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    [ProducesResponseType<StationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StationResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var station = await dbContext.Stations
            .AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new StationResponse(
                candidate.Id,
                candidate.Name,
                candidate.MachineName,
                candidate.IpAddress,
                candidate.AgentVersion,
                candidate.Status,
                candidate.CreatedAtUtc,
                candidate.LastSeenAtUtc,
                candidate.PlayerAuthSessions
                    .Where(session => session.Status == PlayerAuthSessionStatus.Active &&
                                      session.ExpiresAtUtc > now)
                    .Select(session => (Guid?)session.UserId)
                    .FirstOrDefault(),
                candidate.PlayerAuthSessions
                    .Where(session => session.Status == PlayerAuthSessionStatus.Active &&
                                      session.ExpiresAtUtc > now)
                    .Select(session => session.User.Username)
                    .FirstOrDefault(),
                candidate.PlayerAuthSessions
                    .Where(session => session.Status == PlayerAuthSessionStatus.Active &&
                                      session.ExpiresAtUtc > now)
                    .Select(session => (Guid?)session.Id)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(cancellationToken);

        return station is null ? NotFound() : Ok(station);
    }

    [HttpPost("register")]
    [ProducesResponseType<RegisterStationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterStationResponse>> Register(
        RegisterStationRequest request,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || !securityOptions.Value.EnableLegacyStationRegistration)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var machineName = request.MachineName.Trim();
        var station = await dbContext.Stations
            .SingleOrDefaultAsync(candidate => candidate.MachineName == machineName, cancellationToken);

        var isNew = station is null;
        var statusChanged = false;

        if (station is null)
        {
            station = new Station(
                Guid.NewGuid(),
                request.Name,
                machineName,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                request.AgentVersion,
                now);
            dbContext.Stations.Add(station);
            statusChanged = true;
        }
        else
        {
            statusChanged = station.UpdateFromAgent(
                request.Name,
                machineName,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                request.AgentVersion,
                now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Station {StationId} ({MachineName}) {RegistrationAction}",
            station.Id,
            station.MachineName,
            isNew ? "registered" : "re-registered");

        if (statusChanged)
        {
            await PublishStatusChanged(station, cancellationToken);
            await commandService.DispatchPendingCommandsAsync(station.Id, cancellationToken);
        }

        return Ok(new RegisterStationResponse(station.Id, tokenService.Create(station.Id)));
    }

    [HttpPost("enroll")]
    [EnableRateLimiting("enrollment")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<EnrollStationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EnrollStationResponse>> Enroll(
        EnrollStationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await enrollmentService.EnrollAsync(
            request.EnrollmentToken,
            request.StationName,
            request.MachineName,
            request.AgentVersion,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded || result.Identity is null)
        {
            return Unauthorized();
        }

        await PublishStatusChanged(
            await dbContext.Stations.FindAsync([result.Identity.StationId], cancellationToken)
                ?? throw new InvalidOperationException("Enrolled station was not persisted."),
            cancellationToken);
        return Ok(new EnrollStationResponse(
            result.Identity.StationId,
            result.Identity.StationSecret,
            result.Identity.ServerPublicKey,
            result.Identity.SignatureAlgorithm));
    }

    [HttpPost("{stationId:guid}/heartbeat")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Heartbeat(
        Guid stationId,
        HeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var legacyDevelopmentAuthentication =
            environment.IsDevelopment() &&
            securityOptions.Value.EnableLegacyStationRegistration &&
            Request.Headers.TryGetValue(LegacyStationTokenHeader, out var legacyToken) &&
            tokenService.IsValid(legacyToken.ToString(), stationId);

        if (!legacyDevelopmentAuthentication)
        {
            var authentication = await hmacAuthenticator.AuthenticateAsync(
                Request,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            if (!authentication.Succeeded || authentication.StationId != stationId)
            {
                return Unauthorized();
            }
        }

        var station = await dbContext.Stations.FindAsync([stationId], cancellationToken);
        if (station is null)
        {
            return NotFound();
        }

        if (!string.Equals(station.MachineName, request.MachineName.Trim(), StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Heartbeat machine name mismatch for station {StationId}",
                stationId);
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        var statusChanged = station.UpdateFromAgent(
            station.Name,
            request.MachineName,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            request.AgentVersion,
            now);
        station.UpdateClientHealth(request.ClientConnected, request.ClientState, request.ClientLastSeenAtUtc, now);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (statusChanged)
        {
            await PublishStatusChanged(station, cancellationToken);
            await commandService.DispatchPendingCommandsAsync(station.Id, cancellationToken);
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/credentials/revoke")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Security)]
    public async Task<IActionResult> RevokeCredential(Guid id, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var credentials = await dbContext.StationCredentials
            .Where(credential => credential.StationId == id && credential.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        if (credentials.Count == 0)
        {
            return NotFound();
        }

        foreach (var credential in credentials)
        {
            credential.Revoke(now);
        }

        await audit.WriteAsync(
            "StationCredentialRevoked",
            id,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"Active station credential revoked;EmployeeId={Guid.Parse(User.FindFirst("employee_id")!.Value):D}",
            cancellationToken);
        logger.LogWarning("Credential revoked for station {StationId}", id);
        return NoContent();
    }

    private Task PublishStatusChanged(Station station, CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync(
            "StationStatusChanged",
            new StationStatusChangedMessage(
                station.Id,
                station.Name,
                station.Status,
                station.LastSeenAtUtc),
            cancellationToken);
}
