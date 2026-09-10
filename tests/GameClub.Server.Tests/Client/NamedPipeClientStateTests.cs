using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GameClub.Agent.Configuration;
using GameClub.Agent.Services.Client;
using GameClub.Contracts.Client;
using GameClub.Agent.Services.Players;
using GameClub.Agent.Services.Games;
using GameClub.Contracts.Games;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Client;

public sealed class NamedPipeClientStateTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlayniteMenu_UsesTypedAcknowledgement_WithoutBlockingHeartbeat(bool success)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(AppContext.BaseDirectory, $"pipe-games-{Guid.NewGuid():N}.db");
        var security = Options.Create(new SecurityOptions { ProcessedCommandStorePath = path });
        var coordinator = new ClientStateCoordinator(new SqliteClientStateStore(security), new SqlitePlayerSessionStateStore(security),
            Options.Create(new StationOptions { Name = "PC-01" }), TimeProvider.System);
        await coordinator.CompleteSessionReconciliationAsync(null, default);
        using var server = new ClientPipeServer(new WindowsClientPipeServerFactory(), coordinator,
            new FakePlayerLoginService(), NullLogger<ClientPipeServer>.Instance, new FakeGames());
        await server.StartAsync(default);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", ClientPipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pipe.ConnectAsync(4000, timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var id = Guid.NewGuid();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(ClientPipeRequestType.OpenGames,
                OpenGames: new GamesMenuRequest(id)), SerializerOptions));
            var action = JsonSerializer.Deserialize<ClientPipeResponse>((await reader.ReadLineAsync(timeout.Token))!, SerializerOptions)!;
            Assert.Equal(id, action.PlayniteAction!.RequestId);
            Assert.Equal(PlayniteActionType.OpenFullscreen, action.PlayniteAction.Action);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(ClientPipeRequestType.Heartbeat), SerializerOptions));
            var heartbeat = JsonSerializer.Deserialize<ClientPipeResponse>((await reader.ReadLineAsync(timeout.Token))!, SerializerOptions)!;
            Assert.Equal(ClientPipeResponseType.State, heartbeat.Type);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(ClientPipeRequestType.PlayniteActionResult,
                PlayniteResult: new PlayniteActionResult(Guid.NewGuid(), true, null)), SerializerOptions));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(ClientPipeRequestType.PlayniteActionResult,
                PlayniteResult: new PlayniteActionResult(id, success, success ? null : "FAILED")), SerializerOptions));
            var response = JsonSerializer.Deserialize<ClientPipeResponse>((await reader.ReadLineAsync(timeout.Token))!, SerializerOptions)!;
            Assert.Equal(id, response.GamesMenuResult!.RequestId);
            Assert.Equal(success, response.GamesMenuResult.Success);
        }
        finally { await server.StopAsync(default); if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class FakeGames : IAgentGameLaunchService
    {
        public Task<PlayniteActionRequest> PrepareAsync(Guid requestId, Guid? gameId, Guid? expectedSessionId,
            Guid? expectedPlayniteId, DateTime? commandExpiry, CancellationToken ct) =>
            Task.FromResult(new PlayniteActionRequest(requestId, PlayniteActionType.OpenFullscreen, Guid.NewGuid(), DateTime.UtcNow.AddSeconds(10)));
    }

    [Fact]
    public async Task ReconnectedNamedPipeClient_ReceivesCurrentLockedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, $"pipe-state-{Guid.NewGuid():N}.db");
        var security = Options.Create(new SecurityOptions { ProcessedCommandStorePath = path });
        var coordinator = new ClientStateCoordinator(
            new SqliteClientStateStore(security),
            new SqlitePlayerSessionStateStore(security),
            Options.Create(new StationOptions { Name = "PC-01" }),
            TimeProvider.System);
        await coordinator.CompleteSessionReconciliationAsync(null, default);
        await coordinator.SetStateAsync(ClientShellState.Locked, null, default);
        using var server = new ClientPipeServer(
            new WindowsClientPipeServerFactory(),
            coordinator,
            new FakePlayerLoginService(),
            NullLogger<ClientPipeServer>.Instance);

        await server.StartAsync(default);
        try
        {
            var first = await ReadCurrentStateAsync();
            var reconnected = await ReadCurrentStateAsync();

            Assert.Equal(ClientShellState.Locked, first.State);
            Assert.Equal(ClientShellState.Locked, reconnected.State);
            Assert.Equal("PC-01", reconnected.StationName);
        }
        finally
        {
            await server.StopAsync(default);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task PlayerLoginRequest_ProducesSafeResultAndSessionActiveState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, $"pipe-login-{Guid.NewGuid():N}.db");
        var security = Options.Create(new SecurityOptions { ProcessedCommandStorePath = path });
        var coordinator = new ClientStateCoordinator(
            new SqliteClientStateStore(security),
            new SqlitePlayerSessionStateStore(security),
            Options.Create(new StationOptions { Name = "PC-01" }),
            TimeProvider.System);
        await coordinator.CompleteSessionReconciliationAsync(null, default);
        await coordinator.SetStateAsync(ClientShellState.Available, null, default);
        using var server = new ClientPipeServer(
            new WindowsClientPipeServerFactory(),
            coordinator,
            new FakePlayerLoginService(coordinator),
            NullLogger<ClientPipeServer>.Instance);

        await server.StartAsync(default);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                ClientPipeProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(4000, timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            var requestId = Guid.NewGuid();
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ClientPipeRequest(
                    ClientPipeRequestType.PlayerLogin,
                    Login: new PlayerLoginRequest(requestId, "nur", "StrongPassword123!")),
                SerializerOptions));

            var loginResponse = JsonSerializer.Deserialize<ClientPipeResponse>(
                (await reader.ReadLineAsync(timeout.Token))!,
                SerializerOptions);
            var stateResponse = JsonSerializer.Deserialize<ClientPipeResponse>(
                (await reader.ReadLineAsync(timeout.Token))!,
                SerializerOptions);

            Assert.True(loginResponse?.LoginResult?.Success);
            Assert.Equal(requestId, loginResponse?.LoginResult?.RequestId);
            Assert.Equal(ClientShellState.SessionActive, stateResponse?.State?.State);
            Assert.Equal("Nur", stateResponse?.State?.DisplayName);
            Assert.DoesNotContain(
                "StrongPassword123!",
                JsonSerializer.Serialize(loginResponse, SerializerOptions),
                StringComparison.Ordinal);
        }
        finally
        {
            await server.StopAsync(default);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task PendingLogin_DoesNotStarveStateAcknowledgementOrHeartbeat()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, $"pipe-ack-{Guid.NewGuid():N}.db");
        var security = Options.Create(new SecurityOptions { ProcessedCommandStorePath = path });
        var coordinator = new ClientStateCoordinator(
            new SqliteClientStateStore(security), new SqlitePlayerSessionStateStore(security),
            Options.Create(new StationOptions { Name = "PC-01" }), TimeProvider.System);
        await coordinator.CompleteSessionReconciliationAsync(null, default);
        var blockedLogin = new BlockingPlayerLoginService();
        using var server = new ClientPipeServer(new WindowsClientPipeServerFactory(), coordinator,
            blockedLogin, NullLogger<ClientPipeServer>.Instance);
        await server.StartAsync(default);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", ClientPipeProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(4000, timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(
                ClientPipeRequestType.PlayerLogin,
                Login: new PlayerLoginRequest(Guid.NewGuid(), "nur", "StrongPassword123!")), SerializerOptions));
            await blockedLogin.Started.Task.WaitAsync(timeout.Token);

            var locked = await coordinator.GetCurrentAsync(timeout.Token);
            var deliveryTask = server.PublishAsync(locked, waitForAcknowledgement: true, timeout.Token);
            var stateResponse = JsonSerializer.Deserialize<ClientPipeResponse>(
                (await reader.ReadLineAsync(timeout.Token))!, SerializerOptions);
            Assert.Equal(locked.Revision, stateResponse?.State?.Revision);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(
                ClientPipeRequestType.AcknowledgeState, locked.Revision), SerializerOptions));
            var delivery = await deliveryTask.WaitAsync(timeout.Token);
            Assert.True(delivery.Acknowledged);

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ClientPipeRequest(ClientPipeRequestType.Heartbeat), SerializerOptions));
            var heartbeatResponse = JsonSerializer.Deserialize<ClientPipeResponse>(
                (await reader.ReadLineAsync(timeout.Token))!, SerializerOptions);
            Assert.Equal(ClientPipeResponseType.State, heartbeatResponse?.Type);

            var rejectedRequestId = Guid.NewGuid();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ClientPipeRequest(
                ClientPipeRequestType.PlayerLogin,
                Login: new PlayerLoginRequest(rejectedRequestId, "alex", "AnotherPassword123!")), SerializerOptions));
            var busyResponse = JsonSerializer.Deserialize<ClientPipeResponse>(
                (await reader.ReadLineAsync(timeout.Token))!, SerializerOptions);
            Assert.Equal(rejectedRequestId, busyResponse?.LoginResult?.RequestId);
            Assert.Equal("RATE_LIMITED", busyResponse?.LoginResult?.ErrorCode);
        }
        finally
        {
            blockedLogin.Release.TrySetResult(true);
            await server.StopAsync(default);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<ClientStateMessage> ReadCurrentStateAsync()
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            ClientPipeProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(4000, timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(
                new ClientPipeRequest(ClientPipeRequestType.GetState),
                SerializerOptions));
        var line = await reader.ReadLineAsync(timeout.Token);
        var response = JsonSerializer.Deserialize<ClientPipeResponse>(line!, SerializerOptions)
            ?? throw new InvalidOperationException("Agent returned no pipe response.");
        return response.State ?? throw new InvalidOperationException("Agent returned no client state.");
    }

    private sealed class FakePlayerLoginService(IClientStateCoordinator? coordinator = null)
        : IPlayerLoginService
    {
        public async Task<AgentPlayerLoginOutcome> LoginAsync(
            PlayerLoginRequest request,
            CancellationToken cancellationToken)
        {
            if (coordinator is null)
            {
                throw new NotSupportedException();
            }

            var persisted = new PersistedPlayerSession(
                Guid.NewGuid(),
                Guid.NewGuid(),
                request.Username,
                "Nur",
                DateTime.UtcNow,
                DateTime.UtcNow.AddHours(12));
            var state = await coordinator.ActivatePlayerSessionAsync(persisted, cancellationToken);
            return new AgentPlayerLoginOutcome(
                new PlayerLoginResultMessage(
                    request.RequestId,
                    true,
                    null,
                    persisted.SessionId,
                    new ClientPlayerIdentity(
                        persisted.UserId,
                        persisted.Username,
                        persisted.DisplayName)),
                state);
        }

        public Task<AgentPlayerLogoutOutcome> LogoutAsync(
            PlayerLogoutRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientStateMessage> ReconcileCurrentSessionAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientStateMessage> ForceLogoutAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BlockingPlayerLoginService : IPlayerLoginService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentPlayerLoginOutcome> LoginAsync(
            PlayerLoginRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new AgentPlayerLoginOutcome(
                new PlayerLoginResultMessage(request.RequestId, false, "SERVER_UNAVAILABLE", null, null), null);
        }

        public Task<AgentPlayerLogoutOutcome> LogoutAsync(PlayerLogoutRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ClientStateMessage> ReconcileCurrentSessionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ClientStateMessage> ForceLogoutAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
