using GameClub.Contracts.Client;
using GameClub.Contracts.Games;

namespace GameClub.Client.Services.Playnite;

public sealed class PlayniteSessionBridge(IPlayniteConfigurationReader configuration, IPlayniteProcessAdapter process, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _requests = [];
    private readonly Queue<Guid> _requestOrder = new();
    private ClientStateMessage? _state;
    private Guid? _managedSession;
    private bool _closeOnEnd;
    public event Action<bool>? FullscreenChanged;

    public static bool CanPlay(ClientStateMessage? state, DateTime now) =>
        state is { State: ClientShellState.SessionActive, UserId: not null, PlayerAuthSessionId: not null,
            GamingSession: { Status: "Active" } session }
        && session.UserId == state.UserId && (session.TariffId.HasValue || session.PackageId.HasValue)
        && session.ExpectedEndAtUtc > now && session.RemainingSeconds > 0
        && state.TimestampUtc <= now.AddSeconds(10) && state.TimestampUtc > now.AddSeconds(-20);

    public void UpdateState(ClientStateMessage? state)
    {
        lock (_gate)
        {
            _state = state;
            if (_managedSession is not null && (!CanPlay(state, clock.GetUtcNow().UtcDateTime)
                || state!.GamingSession!.Id != _managedSession)) Revoke();
        }
    }

    public void Refresh() { lock (_gate) { if (_managedSession.HasValue && !CanPlay(_state, clock.GetUtcNow().UtcDateTime)) Revoke(); } }

    public PlayniteActionResult Execute(PlayniteActionRequest request)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (request.RequestId == Guid.Empty || !_requests.Add(request.RequestId)) return new(request.RequestId, false, "PLAYNITE_REPLAY");
            _requestOrder.Enqueue(request.RequestId);
            if (_requestOrder.Count > 1024) _requests.Remove(_requestOrder.Dequeue());
            if (!CanPlay(_state, now) || request.GamingSessionId != _state!.GamingSession!.Id
                || request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now + PlaynitePolicy.AuthorizationLifetime
                || !Enum.IsDefined(request.Action)) return new(request.RequestId, false, "GAME_SESSION_REQUIRED");
            try
            {
                var config = configuration.Read();
                if (request.Action == PlayniteActionType.LaunchGame)
                {
                    if (request.GameId is not { } game || request.PlayniteGameId is not { } playnite
                        || !config.AllowedGames.Any(x => x.GameId == game && x.PlayniteGameId == playnite))
                        return new(request.RequestId, false, "GAME_NOT_ALLOWED");
                }
                else if (request.GameId.HasValue || request.PlayniteGameId.HasValue) return new(request.RequestId, false, "INVALID_GAME_REQUEST");
                if (clock.GetUtcNow().UtcDateTime >= request.ExpiresAtUtc) return new(request.RequestId, false, "GAME_AUTHORIZATION_EXPIRED");
                process.Execute(config.FullscreenExecutable, request.PlayniteGameId);
                _managedSession = request.GamingSessionId;
                _closeOnEnd = config.CloseOnSessionEnd;
                FullscreenChanged?.Invoke(true);
                return new(request.RequestId, true, null);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return new(request.RequestId, false, "PLAYNITE_UNAVAILABLE"); }
        }
    }

    private void Revoke()
    {
        _managedSession = null;
        try { if (_closeOnEnd) process.CloseManaged(); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { FullscreenChanged?.Invoke(false); }
    }
}
