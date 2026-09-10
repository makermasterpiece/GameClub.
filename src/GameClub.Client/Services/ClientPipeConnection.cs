using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameClub.Contracts.Client;
using GameClub.Contracts.Games;
using GameClub.Client.Services.Playnite;

namespace GameClub.Client.Services;

public sealed class ClientPipeConnection
{
    public PlayniteSessionBridge? Playnite { get; init; }
    public event Action<GamesMenuResult>? GamesMenuResultReceived;
    public Task SendOpenGamesAsync(GamesMenuRequest request, CancellationToken cancellationToken) =>
        GetActiveSession().SendRequestAsync(new ClientPipeRequest(ClientPipeRequestType.OpenGames, OpenGames: request), cancellationToken);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly object _sessionLock = new();
    private PipeSession? _activeSession;

    public event Action<ClientStateMessage>? StateReceived;
    public event Action<PlayerLoginResultMessage>? LoginResultReceived;
    public event Action<PlayerLogoutResultMessage>? LogoutResultReceived;
    public event Action? AgentUnavailable;

    public Task SendLoginAsync(PlayerLoginRequest request, CancellationToken cancellationToken) =>
        GetActiveSession().SendRequestAsync(
            new ClientPipeRequest(ClientPipeRequestType.PlayerLogin, Login: request),
            cancellationToken);

    public Task SendLogoutAsync(PlayerLogoutRequest request, CancellationToken cancellationToken) =>
        GetActiveSession().SendRequestAsync(
            new ClientPipeRequest(ClientPipeRequestType.PlayerLogout, Logout: request),
            cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning("GameClub Agent pipe unavailable: {0}", exception.GetType().Name);
            }

            AgentUnavailable?.Invoke();
            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            ClientPipeProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new PipeSession(writer);
        SetActiveSession(session);

        var heartbeatTask = Task.CompletedTask;
        Task actionTask = Task.CompletedTask;
        long latestRevision = 0;

        try
        {
            await session.SendRequestAsync(
                new ClientPipeRequest(ClientPipeRequestType.GetState),
                cancellationToken);
            heartbeatTask = SendHeartbeatsAsync(session, connectionLifetime);
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
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
                    throw new IOException("Agent pipe stopped responding.");
                }
                if (line is null)
                {
                    break;
                }

                if (Encoding.UTF8.GetByteCount(line) > ClientPipeProtocol.MaximumMessageBytes)
                {
                    throw new IOException("Agent pipe message exceeded the size limit.");
                }

                var response = JsonSerializer.Deserialize<ClientPipeResponse>(line, SerializerOptions)
                    ?? throw new IOException("Agent returned an empty pipe response.");
                switch (response.Type)
                {
                    case ClientPipeResponseType.State when response.State is not null:
                        var state = response.State;
                        if (state.Revision <= 0 || !Enum.IsDefined(state.State))
                        {
                            throw new IOException("Agent returned an invalid state message.");
                        }

                        if (state.Revision >= latestRevision)
                        {
                            latestRevision = state.Revision;
                            Playnite?.UpdateState(state);
                            StateReceived?.Invoke(state);
                        }

                        await session.SendRequestAsync(
                            new ClientPipeRequest(
                                ClientPipeRequestType.AcknowledgeState,
                                state.Revision),
                            cancellationToken);
                        break;
                    case ClientPipeResponseType.PlayerLoginResult
                        when response.LoginResult is not null:
                        LoginResultReceived?.Invoke(response.LoginResult);
                        break;
                    case ClientPipeResponseType.PlayerLogoutResult
                        when response.LogoutResult is not null:
                        LogoutResultReceived?.Invoke(response.LogoutResult);
                        break;
                    case ClientPipeResponseType.GamesMenuResult when response.GamesMenuResult is not null:
                        GamesMenuResultReceived?.Invoke(response.GamesMenuResult);
                        break;
                    case ClientPipeResponseType.PlayniteAction when response.PlayniteAction is not null:
                        if (!actionTask.IsCompleted) throw new IOException("Overlapping Playnite action.");
                        actionTask = ExecutePlayniteAsync(session, response.PlayniteAction, connectionLifetime.Token);
                        break;
                    default:
                        throw new IOException("Agent returned an invalid pipe response.");
                }
            }
        }
        finally
        {
            session.Disconnect();
            ClearActiveSession(session);
            connectionLifetime.Cancel();
            Playnite?.UpdateState(null);
            try { await actionTask; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task ExecutePlayniteAsync(PipeSession session, PlayniteActionRequest action, CancellationToken token)
    {
        var result = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return Playnite?.Execute(action) ?? new PlayniteActionResult(action.RequestId, false, "PLAYNITE_UNAVAILABLE");
        }, token);
        await session.SendRequestAsync(new ClientPipeRequest(ClientPipeRequestType.PlayniteActionResult, PlayniteResult: result), token);
    }

    private static async Task SendHeartbeatsAsync(
        PipeSession session,
        CancellationTokenSource connectionLifetime)
    {
        var cancellationToken = connectionLifetime.Token;
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await session.SendRequestAsync(
                    new ClientPipeRequest(ClientPipeRequestType.Heartbeat),
                    cancellationToken);
            }
        }
        finally
        {
            // A failed heartbeat write must interrupt the pending read as well.
            connectionLifetime.Cancel();
        }
    }

    private PipeSession GetActiveSession()
    {
        lock (_sessionLock)
        {
            return _activeSession is { Connected: true } session
                ? session
                : throw new IOException("GameClub Agent is not connected.");
        }
    }

    private void SetActiveSession(PipeSession session)
    {
        lock (_sessionLock)
        {
            _activeSession = session;
        }
    }

    private void ClearActiveSession(PipeSession session)
    {
        lock (_sessionLock)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
            }
        }
    }

    private sealed class PipeSession(StreamWriter writer)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private volatile bool _connected = true;

        public bool Connected => _connected;

        public async Task SendRequestAsync(
            ClientPipeRequest request,
            CancellationToken cancellationToken)
        {
            if (!_connected)
            {
                throw new IOException("GameClub Agent is disconnected.");
            }

            var json = JsonSerializer.Serialize(request, SerializerOptions);
            if (Encoding.UTF8.GetByteCount(json) > ClientPipeProtocol.MaximumMessageBytes)
            {
                throw new IOException("Client pipe request exceeded the size limit.");
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
                    throw new IOException("GameClub Agent is disconnected.");
                }

                await writer.WriteLineAsync(json.AsMemory(), sendTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Agent pipe write timed out.");
            }
            finally
            {
                if (lockTaken)
                {
                    _writeLock.Release();
                }
            }
        }

        public void Disconnect() => _connected = false;
    }
}
