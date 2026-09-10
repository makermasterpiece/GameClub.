using GameClub.Application.Abstractions;
using GameClub.Application.Gaming;
using GameClub.Contracts.Games;
using GameClub.Domain.Commands;
using GameClub.Domain.Games;
using GameClub.Domain.Gaming;
using GameClub.Domain.Security;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Games;

public sealed class GameLaunchService(IClubData data, GamingSessionService gaming, TimeProvider clock)
{
    public static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InventoryFreshness = TimeSpan.FromSeconds(90);

    public async Task<GameAuthorizationResponse> AuthorizeAsync(Guid stationId, Guid gamingSessionId, Guid? gameId,
        CancellationToken ct)
    {
        // Settle any due session before opening the authorization transaction; never nest units of work.
        await gaming.CurrentAsync(stationId, ct);
        var outcome = await data.AtomicAsync(async token =>
        {
            try
            {
                var grant = await ValidateCoreAsync(stationId, gamingSessionId, gameId, token);
                Audit(stationId, gamingSessionId, gameId, "GameLaunchAuthorized", null);
                return (Grant: (GameAuthorizationResponse?)grant, Error: (string?)null);
            }
            catch (ClubException ex)
            {
                Audit(stationId, gamingSessionId, gameId, "GameLaunchDenied", ex.Code);
                return (Grant: (GameAuthorizationResponse?)null, Error: ex.Code);
            }
        }, ct);
        return outcome.Grant ?? throw new ClubException(outcome.Error!);
    }

    /// <summary>Read-only validation, joining the caller's transaction. Never fetches cover/executable metadata.</summary>
    public async Task<GameAuthorizationResponse> ValidateCoreAsync(Guid stationId, Guid gamingSessionId, Guid? gameId, CancellationToken ct)
    {
        if (stationId == Guid.Empty || gamingSessionId == Guid.Empty || gameId == Guid.Empty)
            throw new ClubException("INVALID_GAME_AUTHORIZATION");
        var now = clock.GetUtcNow().UtcDateTime;
        var gameSession = await data.Query<GamingSession>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == gamingSessionId &&
            s.StationId == stationId, ct);
        if (gameSession is null || gameSession.Status != GamingSessionStatus.Active || gameSession.BillingMode is null ||
            gameSession.IsExpiredAt(now) || gameSession.ExpectedEndAtUtc is null)
            throw new ClubException("ACTIVE_PAID_SESSION_REQUIRED");
        var auth = await data.Query<PlayerAuthSession>().AsNoTracking().SingleOrDefaultAsync(a =>
            a.StationId == stationId && a.UserId == gameSession.UserId && a.Status == PlayerAuthSessionStatus.Active &&
            a.ExpiresAtUtc > now && a.User.Status == UserStatus.Active, ct);
        if (auth is null) throw new ClubException("PLAYER_NOT_AUTHENTICATED");
        var station = await data.Query<Station>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == stationId, ct);
        if (station?.Status != StationStatus.Online || station.LastSeenAtUtc > now ||
            station.LastSeenAtUtc < now - StationPowerPolicy.HeartbeatFreshness)
            throw new ClubException("STATION_UNAVAILABLE");
        if (!station.ClientConnected || station.ClientState != "SessionActive" ||
            station.ClientLastSeenAtUtc is null || station.ClientLastSeenAtUtc > now ||
            station.ClientLastSeenAtUtc < now - Station.ClientHeartbeatTimeout)
            throw new ClubException("CLIENT_NOT_READY");
        var cutoff = now - StationPowerPolicy.BusyGrace;
        if (await data.Query<AgentCommand>().AnyAsync(c => c.StationId == stationId &&
            (c.Type == AgentCommandType.RestartStation || c.Type == AgentCommandType.ShutdownStation) &&
            c.Status != AgentCommandStatus.Failed && c.Status != AgentCommandStatus.Expired && c.ExpiresAtUtc > cutoff, ct))
            throw new ClubException("STATION_POWER_PENDING");
        var inventoryCutoff = now - InventoryFreshness;
        var available = from report in data.Query<StationGame>().AsNoTracking()
            join game in data.Query<Game>().AsNoTracking() on report.GameId equals game.Id
            where report.StationId == stationId && report.Installed && report.LastDetectedAtUtc >= inventoryCutoff &&
                report.LastDetectedAtUtc <= now && game.IsActive && game.PlayniteGameId != null
            select game;
        Guid? playniteGameId = null;
        if (gameId is { } id)
        {
            var game = await available.SingleOrDefaultAsync(g => g.Id == id, ct);
            if (game is null || !Guid.TryParse(game.PlayniteGameId, out var parsed) || parsed == Guid.Empty)
                throw new ClubException("GAME_UNAVAILABLE");
            playniteGameId = parsed;
        }
        // Fullscreen must bootstrap Playnite before its exporter can refresh inventory. Actual game
        // launches still require the fresh installed report above and the endpoint's local allowlist.
        else if (!await data.Query<Game>().AnyAsync(g => g.IsActive && g.PlayniteGameId != null, ct))
            throw new ClubException("GAME_UNAVAILABLE");

        var expires = new[] { now + AuthorizationLifetime, gameSession.ExpectedEndAtUtc.Value, auth.ExpiresAtUtc }.Min();
        return new GameAuthorizationResponse(gamingSessionId, gameId, playniteGameId, expires);
    }

    private void Audit(Guid stationId, Guid sessionId, Guid? gameId, string type, string? reason) =>
        data.Add(new SecurityAuditEvent(Guid.NewGuid(), type, stationId, clock.GetUtcNow().UtcDateTime, null,
            $"GamingSessionId={sessionId:D};GameId={gameId:D};Reason={reason ?? "Success"}"));
}
