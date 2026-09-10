using GameClub.Agent.Configuration;
using GameClub.Contracts.Client;
using GameClub.Contracts.Gaming;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Services.Client;

public interface IClientStateCoordinator
{
    bool ClientConnected { get; }
    DateTime? ClientLastSeenAtUtc { get; }
    bool SessionReconciliationCompleted { get; }
    Task<ClientStateMessage> GetCurrentAsync(CancellationToken cancellationToken);
    Task<ClientStateMessage> SetStateAsync(
        ClientShellState state,
        string? message,
        CancellationToken cancellationToken);
    Task<ClientStateMessage> ActivatePlayerSessionAsync(
        PersistedPlayerSession session,
        CancellationToken cancellationToken);
    Task<ClientStateMessage> CompleteSessionReconciliationAsync(
        PersistedPlayerSession? session,
        CancellationToken cancellationToken);
    Task<ClientStateMessage> CompleteSessionReconciliationAsync(
        PersistedPlayerSession? session,
        GamingSessionSnapshot? gamingSession,
        CancellationToken cancellationToken);
    Task<ClientStateMessage> MarkServerUnavailableAsync(CancellationToken cancellationToken);
    Task<ClientStateMessage> ClearPlayerSessionAsync(CancellationToken cancellationToken);
    void MarkClientConnected();
    void MarkClientHeartbeat();
    void MarkClientDisconnected();
}

public sealed class ClientStateCoordinator(
    IClientStateStore store,
    IPlayerSessionStateStore playerSessionStore,
    IOptions<StationOptions> stationOptions,
    TimeProvider timeProvider) : IClientStateCoordinator
{
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly object _healthLock = new();
    private readonly string _stationName = GetStationName(stationOptions.Value);
    private PersistedClientState? _current;
    private volatile bool _initialized;
    private PersistedPlayerSession? _playerSession;
    private GamingSessionSnapshot? _gamingSession;
    private bool _sessionReconciliationCompleted;
    private long _revision;
    private bool _clientConnected;
    private DateTime? _clientLastSeenAtUtc;

    public bool SessionReconciliationCompleted
    {
        get
        {
            lock (_healthLock)
            {
                return _sessionReconciliationCompleted;
            }
        }
    }

    public bool ClientConnected
    {
        get
        {
            lock (_healthLock)
            {
                return _clientConnected;
            }
        }
    }

    public DateTime? ClientLastSeenAtUtc
    {
        get
        {
            lock (_healthLock)
            {
                return _clientLastSeenAtUtc;
            }
        }
    }

    public async Task<ClientStateMessage> GetCurrentAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            return ToMessage(Interlocked.Read(ref _revision), null);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ClientStateMessage> SetStateAsync(
        ClientShellState state,
        string? message,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(state) || state is ClientShellState.Offline or ClientShellState.SessionActive)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        await EnsureInitializedAsync(cancellationToken);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await store.SaveAsync(state, now, cancellationToken);
            _current = new PersistedClientState(state, now);
            var revision = Interlocked.Increment(ref _revision);
            return ToMessage(revision, message);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ClientStateMessage> ActivatePlayerSessionAsync(
        PersistedPlayerSession session,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_current!.State != ClientShellState.Available)
            {
                throw new InvalidOperationException("Client shell is not available for player login.");
            }

            await playerSessionStore.SaveAsync(session, cancellationToken);
            _playerSession = session;
            _gamingSession = null;
            SetSessionReconciliationCompleted();
            return ToMessage(Interlocked.Increment(ref _revision), null);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public Task<ClientStateMessage> CompleteSessionReconciliationAsync(
        PersistedPlayerSession? session,
        CancellationToken cancellationToken) =>
        CompleteSessionReconciliationAsync(session, null, cancellationToken);

    public async Task<ClientStateMessage> CompleteSessionReconciliationAsync(
        PersistedPlayerSession? session,
        GamingSessionSnapshot? gamingSession,
        CancellationToken cancellationToken)
    {
        if (gamingSession is not null && (session is null || session.UserId != gamingSession.UserId))
        {
            throw new InvalidOperationException("Gaming session does not match the authenticated player.");
        }

        await EnsureInitializedAsync(cancellationToken);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (session is null)
            {
                await playerSessionStore.ClearAsync(cancellationToken);
            }
            else
            {
                await playerSessionStore.SaveAsync(session, cancellationToken);
            }

            _playerSession = session;
            _gamingSession = gamingSession;
            SetSessionReconciliationCompleted();
            return ToMessage(Interlocked.Increment(ref _revision), null);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ClientStateMessage> ClearPlayerSessionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            await playerSessionStore.ClearAsync(cancellationToken);
            _playerSession = null;
            _gamingSession = null;
            SetSessionReconciliationCompleted();
            return ToMessage(Interlocked.Increment(ref _revision), null);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ClientStateMessage> MarkServerUnavailableAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            // Keep the durable base/auth state, but never authorize access from an unverified cache.
            lock (_healthLock)
            {
                _sessionReconciliationCompleted = false;
            }

            return ToMessage(Interlocked.Increment(ref _revision), "Нет связи с сервером");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void MarkClientConnected()
    {
        lock (_healthLock)
        {
            _clientConnected = true;
            _clientLastSeenAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    public void MarkClientHeartbeat()
    {
        lock (_healthLock)
        {
            if (_clientConnected)
            {
                _clientLastSeenAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            }
        }
    }

    public void MarkClientDisconnected()
    {
        lock (_healthLock)
        {
            _clientConnected = false;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            _current = await store.LoadAsync(cancellationToken);
            _playerSession = await playerSessionStore.LoadAsync(cancellationToken);
            if (_current is null)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                _current = new PersistedClientState(ClientShellState.Locked, now);
                await store.SaveAsync(_current.State, _current.UpdatedAtUtc, cancellationToken);
            }

            Interlocked.Exchange(ref _revision, 1);
            _initialized = true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private ClientStateMessage ToMessage(long revision, string? message)
    {
        if (!SessionReconciliationCompleted)
        {
            return new ClientStateMessage(
                ClientShellState.Offline,
                _stationName,
                timeProvider.GetUtcNow().UtcDateTime,
                message ?? "Проверка сессии на сервере",
                revision);
        }

        var state = _current!;
        if (state.State is ClientShellState.Locked or ClientShellState.Maintenance)
        {
            return new ClientStateMessage(
                state.State,
                _stationName,
                state.UpdatedAtUtc,
                message,
                revision);
        }

        if (_playerSession is not null)
        {
            return new ClientStateMessage(
                ClientShellState.SessionActive,
                _stationName,
                _playerSession.CreatedAtUtc,
                message,
                revision,
                _playerSession.UserId,
                _playerSession.Username,
                _playerSession.DisplayName,
                _playerSession.SessionId,
                _gamingSession);
        }

        var effectiveState = state.State == ClientShellState.SessionActive
            ? ClientShellState.Available
            : state.State;
        return new ClientStateMessage(
            effectiveState,
            _stationName,
            state.UpdatedAtUtc,
            message,
            revision);
    }

    private void SetSessionReconciliationCompleted()
    {
        lock (_healthLock)
        {
            _sessionReconciliationCompleted = true;
        }
    }

    private static string GetStationName(StationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            throw new InvalidOperationException("Station:Name must be configured.");
        }

        return options.Name.Trim();
    }
}
