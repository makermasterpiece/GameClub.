using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameClub.Contracts.Client;
using GameClub.Agent.Services.Players;
using GameClub.Agent.Services.Games;
using GameClub.Contracts.Games;

namespace GameClub.Agent.Services.Client;

public sealed class ClientPipeServer(
    IClientPipeServerFactory pipeFactory,
    IClientStateCoordinator stateCoordinator,
    IPlayerLoginService playerLoginService,
    ILogger<ClientPipeServer> logger,
    IAgentGameLaunchService? games = null) : BackgroundService, IClientStateNotifier, IPlayniteActionTransport
{
    private static readonly TimeSpan StateAcknowledgementTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _sessionLock = new();
    private ClientSession? _activeSession;

    public Task<PlayniteActionResult> ExecutePlayniteAsync(PlayniteActionRequest request, CancellationToken ct)
    {
        ClientSession? session;
        lock (_sessionLock) session = _activeSession;
        return session is null
            ? Task.FromResult(new PlayniteActionResult(request.RequestId, false, "CLIENT_DISCONNECTED"))
            : session.ExecutePlayniteAsync(request, ct);
    }

    public Task<ClientStateDelivery> PublishAsync(
        ClientStateMessage state,
        bool waitForAcknowledgement,
        CancellationToken cancellationToken)
    {
        ClientSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
        }

        return session is null
            ? Task.FromResult(new ClientStateDelivery(false, false))
            : session.SendStateAsync(state, waitForAcknowledgement, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await stateCoordinator.GetCurrentAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = pipeFactory.Create();
                await pipe.WaitForConnectionAsync(stoppingToken);
                await ProcessClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning("Client named-pipe connection failed ({ErrorType}); accepting a new connection", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ProcessClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        var session = new ClientSession(writer, StateAcknowledgementTimeout);
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var playerOperation = Task.CompletedTask;

        lock (_sessionLock)
        {
            _activeSession = session;
        }

        stateCoordinator.MarkClientConnected();
        logger.LogInformation("GameClub Client connected to local named pipe");

        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                string? line;
                try
                {
                    line = await BoundedPipeReader.ReadLineAsync(reader, readTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Client pipe stopped responding.");
                }
                if (line is null)
                {
                    break;
                }

                if (Encoding.UTF8.GetByteCount(line) > ClientPipeProtocol.MaximumMessageBytes)
                {
                    throw new IOException("Client pipe request exceeded the size limit.");
                }

                var request = JsonSerializer.Deserialize<ClientPipeRequest>(line, SerializerOptions)
                    ?? throw new IOException("Client pipe request was empty.");
                switch (request.Type)
                {
                    case ClientPipeRequestType.GetState:
                        EnsureRequestShape(request, allowRevision: false);
                        var current = await stateCoordinator.GetCurrentAsync(cancellationToken);
                        await session.SendStateAsync(current, waitForAcknowledgement: false, cancellationToken);
                        break;
                    case ClientPipeRequestType.Heartbeat:
                        EnsureRequestShape(request, allowRevision: false);
                        stateCoordinator.MarkClientHeartbeat();
                        var heartbeatState = await stateCoordinator.GetCurrentAsync(cancellationToken);
                        await session.SendStateAsync(heartbeatState, waitForAcknowledgement: false, cancellationToken);
                        break;
                    case ClientPipeRequestType.AcknowledgeState
                        when request.Revision is > 0 and long revision &&
                             request.Login is null && request.Logout is null && request.OpenGames is null && request.PlayniteResult is null:
                        stateCoordinator.MarkClientHeartbeat();
                        session.Acknowledge(revision);
                        break;
                    case ClientPipeRequestType.PlayerLogin
                        when request.Login is not null &&
                             request.Revision is null && request.Logout is null && request.OpenGames is null && request.PlayniteResult is null:
                    case ClientPipeRequestType.PlayerLogout
                        when request.Logout is not null &&
                             request.Revision is null && request.Login is null && request.OpenGames is null && request.PlayniteResult is null:
                        if (!playerOperation.IsCompleted)
                        {
                            await session.SendResponseAsync(PlayerFailure(request, "RATE_LIMITED"), cancellationToken);
                            break;
                        }

                        // Only one credential-bearing operation is retained. Keep reading ACK/heartbeat
                        // frames while the HTTP operation is pending.
                        playerOperation = ProcessPlayerRequestAsync(request, session, connectionLifetime);
                        break;
                    case ClientPipeRequestType.OpenGames when request.OpenGames is { RequestId: var menuId } &&
                        menuId != Guid.Empty && request.Revision is null && request.Login is null && request.Logout is null && request.PlayniteResult is null:
                        if (!playerOperation.IsCompleted)
                        {
                            await session.SendResponseAsync(new ClientPipeResponse(ClientPipeResponseType.GamesMenuResult,
                                GamesMenuResult: new GamesMenuResult(menuId, false, "RATE_LIMITED")), cancellationToken);
                            break;
                        }
                        playerOperation = ProcessGamesRequestAsync(menuId, session, connectionLifetime);
                        break;
                    case ClientPipeRequestType.PlayniteActionResult when request.PlayniteResult is { } gameResult &&
                        gameResult.RequestId != Guid.Empty && (gameResult.ErrorCode?.Length ?? 0) <= 80 &&
                        request.Revision is null && request.Login is null && request.Logout is null && request.OpenGames is null:
                        session.AcknowledgePlaynite(gameResult);
                        break;
                    default:
                        throw new IOException("Client pipe request type is invalid.");
                }
            }
        }
        finally
        {
            connectionLifetime.Cancel();
            session.Disconnect();
            lock (_sessionLock)
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }

            stateCoordinator.MarkClientDisconnected();
            logger.LogInformation("GameClub Client disconnected from local named pipe");
            await playerOperation;
        }
    }

    private async Task ProcessPlayerRequestAsync(
        ClientPipeRequest request,
        ClientSession session,
        CancellationTokenSource connectionLifetime)
    {
        var cancellationToken = connectionLifetime.Token;
        try
        {
            ClientStateMessage? state;
            ClientPipeResponse response;
            if (request.Login is { } loginRequest)
            {
                var login = await playerLoginService.LoginAsync(loginRequest, cancellationToken);
                response = new ClientPipeResponse(ClientPipeResponseType.PlayerLoginResult, LoginResult: login.Result);
                state = login.State;
            }
            else
            {
                var logout = await playerLoginService.LogoutAsync(request.Logout!, cancellationToken);
                response = new ClientPipeResponse(ClientPipeResponseType.PlayerLogoutResult, LogoutResult: logout.Result);
                state = logout.State;
            }

            await session.SendResponseAsync(response, cancellationToken);
            if (state is not null)
            {
                await session.SendStateAsync(state, waitForAcknowledgement: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning("Client player operation failed ({ErrorType})", exception.GetType().Name);
            try
            {
                await session.SendResponseAsync(PlayerFailure(request, "SERVER_ERROR"), cancellationToken);
            }
            catch (Exception)
            {
                connectionLifetime.Cancel();
            }
        }
    }

    private async Task ProcessGamesRequestAsync(Guid id, ClientSession session, CancellationTokenSource lifetime)
    {
        var success = false;
        try
        {
            if (games is null) throw new InvalidOperationException("PLAYNITE_UNAVAILABLE");
            var action = await games.PrepareAsync(id, null, null, null, null, lifetime.Token);
            var result = await session.ExecutePlayniteAsync(action, lifetime.Token);
            success = result.Success;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            logger.LogWarning("Games menu request failed ({ErrorType})", exception.GetType().Name);
        }
        try
        {
            await session.SendResponseAsync(new ClientPipeResponse(ClientPipeResponseType.GamesMenuResult,
                GamesMenuResult: new GamesMenuResult(id, success, success ? null : "GAME_LAUNCH_UNAVAILABLE")), lifetime.Token);
        }
        catch (Exception) { lifetime.Cancel(); }
    }

    private static ClientPipeResponse PlayerFailure(ClientPipeRequest request, string code) =>
        request.Login is { } login
            ? new ClientPipeResponse(ClientPipeResponseType.PlayerLoginResult,
                LoginResult: new PlayerLoginResultMessage(login.RequestId, false, code, null, null))
            : new ClientPipeResponse(ClientPipeResponseType.PlayerLogoutResult,
                LogoutResult: new PlayerLogoutResultMessage(request.Logout!.RequestId, false, code));

    private static void EnsureRequestShape(ClientPipeRequest request, bool allowRevision)
    {
        if ((!allowRevision && request.Revision is not null) ||
            request.Login is not null || request.Logout is not null || request.OpenGames is not null || request.PlayniteResult is not null)
        {
            throw new IOException("Client pipe request shape is invalid.");
        }
    }

    private sealed class ClientSession(StreamWriter writer, TimeSpan acknowledgementTimeout)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _acknowledgements = new();
        private volatile bool _connected = true;
        private readonly SemaphoreSlim _gameLock = new(1, 1);
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PlayniteActionResult>> _gameResults = new();

        public async Task<PlayniteActionResult> ExecutePlayniteAsync(PlayniteActionRequest request, CancellationToken ct)
        {
            if (!await _gameLock.WaitAsync(0, ct)) return new(request.RequestId, false, "RATE_LIMITED");
            var completion = new TaskCompletionSource<PlayniteActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (!_gameResults.TryAdd(request.RequestId, completion)) return new(request.RequestId, false, "DUPLICATE_REQUEST");
                await SendResponseAsync(new ClientPipeResponse(ClientPipeResponseType.PlayniteAction, PlayniteAction: request), ct);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
            {
                return new(request.RequestId, false, "CLIENT_UNAVAILABLE");
            }
            finally
            {
                _gameResults.TryRemove(request.RequestId, out _);
                _gameLock.Release();
            }
        }

        public void AcknowledgePlaynite(PlayniteActionResult result)
        {
            if (_gameResults.TryGetValue(result.RequestId, out var completion))
                completion.TrySetResult(new(result.RequestId, result.Success, result.Success ? null : "PLAYNITE_LAUNCH_FAILED"));
        }

        public async Task<ClientStateDelivery> SendStateAsync(
            ClientStateMessage state,
            bool waitForAcknowledgement,
            CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                return new ClientStateDelivery(false, false);
            }

            TaskCompletionSource<bool>? acknowledgement = null;
            if (waitForAcknowledgement)
            {
                acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_acknowledgements.TryAdd(state.Revision, acknowledgement))
                {
                    throw new InvalidOperationException("Duplicate client state revision.");
                }
            }

            try
            {
                await SendResponseAsync(
                    new ClientPipeResponse(ClientPipeResponseType.State, State: state),
                    cancellationToken);

                if (acknowledgement is null)
                {
                    return new ClientStateDelivery(true, false);
                }

                try
                {
                    var acknowledged = await acknowledgement.Task.WaitAsync(
                        acknowledgementTimeout,
                        cancellationToken);
                    return new ClientStateDelivery(acknowledged, acknowledged);
                }
                catch (TimeoutException)
                {
                    return new ClientStateDelivery(true, false);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return new ClientStateDelivery(false, false);
            }
            finally
            {
                if (acknowledgement is not null)
                {
                    _acknowledgements.TryRemove(state.Revision, out _);
                }
            }
        }

        public async Task SendResponseAsync(
            ClientPipeResponse response,
            CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                throw new IOException("GameClub Client is disconnected.");
            }

            var json = JsonSerializer.Serialize(response, SerializerOptions);
            if (Encoding.UTF8.GetByteCount(json) > ClientPipeProtocol.MaximumMessageBytes)
            {
                throw new IOException("Client pipe response exceeded the size limit.");
            }

            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var lockTaken = false;
            try
            {
                await _writeLock.WaitAsync(sendTimeout.Token);
                lockTaken = true;
                if (!_connected)
                {
                    throw new IOException("GameClub Client is disconnected.");
                }

                await writer.WriteLineAsync(json.AsMemory(), sendTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Client pipe write timed out.");
            }
            finally
            {
                if (lockTaken)
                {
                    _writeLock.Release();
                }
            }
        }

        public void Acknowledge(long revision)
        {
            if (_acknowledgements.TryRemove(revision, out var acknowledgement))
            {
                acknowledgement.TrySetResult(true);
            }
        }

        public void Disconnect()
        {
            _connected = false;
            foreach (var acknowledgement in _acknowledgements.Values)
            {
                acknowledgement.TrySetResult(false);
            }

            _acknowledgements.Clear();
            foreach (var pending in _gameResults)
                pending.Value.TrySetResult(new(pending.Key, false, "CLIENT_DISCONNECTED"));
            _gameResults.Clear();
        }
    }
}
