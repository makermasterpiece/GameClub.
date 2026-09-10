using GameClub.Agent.Security;
using GameClub.Agent.Services.Client;
using GameClub.Agent.Services.Players;
using GameClub.Contracts.Client;
using GameClub.Contracts.Games;

namespace GameClub.Agent.Services.Games;

public interface IAgentGameLaunchService
{
    Task<PlayniteActionRequest> PrepareAsync(Guid requestId, Guid? gameId, Guid? expectedSessionId,
        Guid? expectedPlayniteId, DateTime? commandExpiry, CancellationToken ct);
}

public interface IPlayniteActionTransport
{
    Task<PlayniteActionResult> ExecutePlayniteAsync(PlayniteActionRequest request, CancellationToken ct);
}

public sealed class AgentGameLaunchService(
    ILocalPlayniteLibrary library, StationApiClient api, IStationCredentialStore credentials,
    IClientStateCoordinator state, IPlayerLoginService players, TimeProvider clock) : IAgentGameLaunchService
{
    public async Task<PlayniteActionRequest> PrepareAsync(Guid requestId, Guid? gameId, Guid? expectedSessionId,
        Guid? expectedPlayniteId, DateTime? commandExpiry, CancellationToken ct)
    {
        if (requestId == Guid.Empty || gameId == Guid.Empty || expectedSessionId == Guid.Empty || expectedPlayniteId == Guid.Empty)
            throw new InvalidOperationException("INVALID_GAME_REQUEST");
        var current = await players.ReconcileCurrentSessionAsync(ct);
        RequireSession(current, expectedSessionId);
        var sessionId = current.GamingSession!.Id;
        if (gameId is null) await library.RequireAvailableAsync(ct);
        var credential = await credentials.LoadAsync(ct) ?? throw new InvalidOperationException("AGENT_NOT_ENROLLED");
        var grant = await api.AuthorizeGameAsync(credential, new GameAuthorizationRequest(sessionId, gameId), ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (grant.GamingSessionId != sessionId || grant.GameId != gameId ||
            grant.ExpiresAtUtc.Kind != DateTimeKind.Utc || grant.ExpiresAtUtc <= now ||
            grant.ExpiresAtUtc > now + PlaynitePolicy.AuthorizationLifetime ||
            (gameId is null ? grant.PlayniteGameId is not null : grant.PlayniteGameId is null))
        {
            throw new InvalidOperationException("INVALID_GAME_AUTHORIZATION");
        }
        if (gameId is { } id)
        {
            if (grant.PlayniteGameId == Guid.Empty || (expectedPlayniteId is not null && grant.PlayniteGameId != expectedPlayniteId))
                throw new InvalidOperationException("GAME_MAPPING_CHANGED");
            await library.RequireInstalledAsync(id, grant.PlayniteGameId!.Value, ct);
        }
        RequireSession(await state.GetCurrentAsync(ct), sessionId);
        var expiry = commandExpiry is { } deadline && deadline < grant.ExpiresAtUtc ? deadline : grant.ExpiresAtUtc;
        if (expiry <= clock.GetUtcNow().UtcDateTime) throw new InvalidOperationException("GAME_AUTHORIZATION_EXPIRED");
        return new PlayniteActionRequest(requestId, gameId is null ? PlayniteActionType.OpenFullscreen : PlayniteActionType.LaunchGame,
            sessionId, expiry, gameId, grant.PlayniteGameId);
    }

    private void RequireSession(ClientStateMessage current, Guid? expected)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!state.SessionReconciliationCompleted || !state.ClientConnected ||
            state.ClientLastSeenAtUtc is not { } seen || seen > now || now - seen > TimeSpan.FromSeconds(15) ||
            current.State != ClientShellState.SessionActive || current.PlayerAuthSessionId is null ||
            current.GamingSession is not { Status: "Active", ExpectedEndAtUtc: { } end } session || end <= now ||
            current.UserId != session.UserId || (session.TariffId is null && session.PackageId is null) ||
            (expected is not null && session.Id != expected))
            throw new InvalidOperationException("ACTIVE_PAID_SESSION_REQUIRED");
    }
}
