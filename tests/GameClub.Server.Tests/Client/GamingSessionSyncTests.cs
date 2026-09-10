using System.Net;
using System.Net.Http.Json;
using GameClub.Agent.Configuration;
using GameClub.Agent.Models;
using GameClub.Agent.Security;
using GameClub.Agent.Services;
using GameClub.Agent.Services.Client;
using GameClub.Agent.Services.Players;
using GameClub.Contracts.Client;
using GameClub.Contracts.Gaming;
using GameClub.Domain.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Client;

public sealed class GamingSessionSyncTests
{
    [Fact]
    public async Task Reconciliation_UsesStationHmacAndPublishesServerGamingSnapshot()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();

        var state = await fixture.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.SessionActive, state.State);
        Assert.Equal(fixture.Gaming, state.GamingSession);
        Assert.Equal(fixture.UserId, state.UserId);
        Assert.Equal(new[] { "/api/agent/gaming/session/current", "/api/agent/player/session/current" },
            fixture.Paths);
        Assert.Equal(2, fixture.ValidHmacRequests);
    }

    [Fact]
    public async Task ServerEndsGamingAndAuth_ReconciliationReturnsAvailable()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();
        await fixture.Service.ReconcileCurrentSessionAsync(default);
        fixture.Gaming = null;
        fixture.Authenticated = false;

        var state = await fixture.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.Available, state.State);
        Assert.Null(state.GamingSession);
        Assert.Null(state.PlayerAuthSessionId);
        Assert.Null(fixture.PlayerStore.Value);
    }

    [Fact]
    public async Task AuthWithoutGamingSession_RemainsLoggedInWithoutTimer()
    {
        using var fixture = new Fixture { Gaming = null };
        await fixture.PrepareAvailableAsync();

        var state = await fixture.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.SessionActive, state.State);
        Assert.NotNull(state.PlayerAuthSessionId);
        Assert.Null(state.GamingSession);
    }

    [Fact]
    public async Task AuthRevoked_StaleGamingResponseCannotAuthorizeClient()
    {
        using var fixture = new Fixture { Authenticated = false };
        await fixture.PrepareAvailableAsync();

        var state = await fixture.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.Available, state.State);
        Assert.Null(state.GamingSession);
    }

    [Fact]
    public async Task ConnectionFailure_FailsClosedWithoutDeletingRetainedAuthState()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();
        await fixture.Service.ReconcileCurrentSessionAsync(default);
        var persisted = fixture.PlayerStore.Value;
        fixture.ServerUnavailable = true;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.ReconcileCurrentSessionAsync(default));
        var offline = await fixture.Coordinator.GetCurrentAsync(default);

        Assert.Equal(ClientShellState.Offline, offline.State);
        Assert.Null(offline.GamingSession);
        Assert.Equal(persisted, fixture.PlayerStore.Value);
        fixture.ServerUnavailable = false;
        var restored = await fixture.Service.ReconcileCurrentSessionAsync(default);
        Assert.Equal(ClientShellState.SessionActive, restored.State);
        Assert.Equal(fixture.Gaming, restored.GamingSession);
    }

    [Fact]
    public async Task SnapshotForOtherStation_IsRejectedAndFailsClosed()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();
        fixture.Gaming = fixture.Gaming! with { StationId = Guid.NewGuid() };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.ReconcileCurrentSessionAsync(default));

        Assert.Equal(ClientShellState.Offline, (await fixture.Coordinator.GetCurrentAsync(default)).State);
    }

    [Fact]
    public async Task SnapshotForOtherPlayer_IsRejectedAndFailsClosed()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();
        fixture.Gaming = fixture.Gaming! with { UserId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ReconcileCurrentSessionAsync(default));

        Assert.Equal(ClientShellState.Offline, (await fixture.Coordinator.GetCurrentAsync(default)).State);
    }

    [Fact]
    public async Task LockedBaseState_TakesPrecedenceOverActiveGaming()
    {
        using var fixture = new Fixture();

        var state = await fixture.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.Locked, state.State);
        Assert.Null(state.GamingSession);
    }

    [Fact]
    public async Task RestartedAgent_DoesNotExposeCachedAuthOrGamingBeforeServerValidation()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();
        await fixture.Service.ReconcileCurrentSessionAsync(default);
        var restarted = new ClientStateCoordinator(fixture.BaseStore, fixture.PlayerStore,
            Options.Create(new StationOptions { Name = "PC-01" }), TimeProvider.System);

        var state = await restarted.GetCurrentAsync(default);

        Assert.Equal(ClientShellState.Offline, state.State);
        Assert.Null(state.PlayerAuthSessionId);
        Assert.Null(state.GamingSession);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Paused")]
    public async Task TransferInvalidation_ClearsSourceAndReconcilesSameSessionAtTarget(string status)
    {
        using var source = new Fixture();
        using var target = new Fixture(source.UserId) { Authenticated = false, Gaming = null };
        await source.PrepareAvailableAsync();
        await target.PrepareAvailableAsync();
        source.Gaming = source.Gaming! with { Status = status };
        await source.Service.ReconcileCurrentSessionAsync(default);
        await target.Service.ReconcileCurrentSessionAsync(default);

        var transferred = source.Gaming! with { StationId = target.StationId };
        source.Gaming = null;
        source.Authenticated = false;
        target.Gaming = transferred;
        target.Authenticated = true;
        target.AuthId = source.AuthId;

        var sourceState = await source.Service.ReconcileCurrentSessionAsync(default);
        var targetState = await target.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.Available, sourceState.State);
        Assert.Null(sourceState.GamingSession);
        Assert.Null(sourceState.PlayerAuthSessionId);
        Assert.Null(source.PlayerStore.Value);
        Assert.Equal(ClientShellState.SessionActive, targetState.State);
        Assert.Equal(transferred, targetState.GamingSession);
        Assert.Equal(source.UserId, targetState.UserId);
        Assert.Equal(target.AuthId, targetState.PlayerAuthSessionId);
        Assert.Equal(source.AuthId, targetState.PlayerAuthSessionId);
        Assert.Equal(4, source.ValidHmacRequests);
        Assert.Equal(4, target.ValidHmacRequests);
    }

    [Fact]
    public async Task SourceReconnectAfterTransfer_DoesNotRestoreItsCachedPlayerOrTimer()
    {
        using var source = new Fixture();
        await source.PrepareAvailableAsync();
        await source.Service.ReconcileCurrentSessionAsync(default);
        source.ServerUnavailable = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => source.Service.ReconcileCurrentSessionAsync(default));
        Assert.NotNull(source.PlayerStore.Value);

        // Transfer committed while this agent could not receive an invalidation.
        source.Gaming = null;
        source.Authenticated = false;
        source.ServerUnavailable = false;
        var state = await source.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.Available, state.State);
        Assert.Null(state.UserId);
        Assert.Null(state.GamingSession);
        Assert.Null(source.PlayerStore.Value);
    }

    [Fact]
    public async Task TransferBetweenGamingAndAuthReads_DiscardsStaleSourceGamingSnapshot()
    {
        using var source = new Fixture();
        await source.PrepareAvailableAsync();
        await source.Service.ReconcileCurrentSessionAsync(default);
        source.AfterGamingRead = () =>
        {
            source.Gaming = null;
            source.Authenticated = false;
        };

        var state = await source.Service.ReconcileCurrentSessionAsync(default);

        Assert.Equal(ClientShellState.Available, state.State);
        Assert.Null(state.GamingSession);
        Assert.Null(state.PlayerAuthSessionId);
        Assert.Null(source.PlayerStore.Value);
    }

    [Fact]
    public async Task NewOccupantBetweenReads_CannotInheritPreviousPlayersGamingSnapshot()
    {
        using var source = new Fixture();
        await source.PrepareAvailableAsync();
        await source.Service.ReconcileCurrentSessionAsync(default);
        var nextUser = Guid.NewGuid();
        source.AfterGamingRead = () =>
        {
            source.Gaming = null;
            source.UserId = nextUser;
            source.AuthId = Guid.NewGuid();
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.Service.ReconcileCurrentSessionAsync(default));
        var rejected = await source.Coordinator.GetCurrentAsync(default);
        Assert.Equal(ClientShellState.Offline, rejected.State);
        Assert.Null(rejected.UserId);
        Assert.Null(rejected.GamingSession);

        source.AfterGamingRead = null;
        var reconciled = await source.Service.ReconcileCurrentSessionAsync(default);
        Assert.Equal(nextUser, reconciled.UserId);
        Assert.Equal(source.AuthId, reconciled.PlayerAuthSessionId);
        Assert.Null(reconciled.GamingSession);
    }

    [Fact]
    public async Task InvalidationBurst_CoalescesWakeupsWithoutCarryingAuthoritativeState()
    {
        var signal = new StationSessionSyncSignal();
        for (var index = 0; index < 1000; index++) signal.Notify();
        await signal.WaitAsync(Timeout.InfiniteTimeSpan, default);

        using var cancellation = new CancellationTokenSource();
        var nextWakeup = signal.WaitAsync(Timeout.InfiniteTimeSpan, cancellation.Token);
        Assert.False(nextWakeup.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextWakeup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedLogout_UsesCapturedIdentityAndReconcilesReplacementOccupant(bool forced)
    {
        using var source = new Fixture();
        await source.PrepareAvailableAsync();
        await source.Service.ReconcileCurrentSessionAsync(default);
        var previousAuthId = source.AuthId;
        var replacementUser = Guid.NewGuid();
        source.BeforeLogout = () =>
        {
            source.Gaming = null;
            source.UserId = replacementUser;
            source.AuthId = Guid.NewGuid();
        };

        ClientStateMessage state;
        if (forced)
            state = await source.Service.ForceLogoutAsync(default);
        else
        {
            var outcome = await source.Service.LogoutAsync(new PlayerLogoutRequest(Guid.NewGuid()), default);
            Assert.True(outcome.Result.Success);
            state = Assert.IsType<ClientStateMessage>(outcome.State);
        }

        Assert.Equal(previousAuthId, Assert.Single(source.LogoutSessionIds));
        Assert.True(source.Authenticated);
        Assert.Equal(replacementUser, state.UserId);
        Assert.Equal(source.AuthId, state.PlayerAuthSessionId);
        Assert.Null(state.GamingSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutWithoutVerifiedIdentity_ServerUnavailableFailsClosedWithoutMutation(bool forced)
    {
        using var fixture = new Fixture { ServerUnavailable = true };
        if (forced)
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.ForceLogoutAsync(default));
        else
        {
            var outcome = await fixture.Service.LogoutAsync(new PlayerLogoutRequest(Guid.NewGuid()), default);
            Assert.False(outcome.Result.Success);
            Assert.Equal("SERVER_UNAVAILABLE", outcome.Result.ErrorCode);
        }

        Assert.Empty(fixture.LogoutSessionIds);
        var state = await fixture.Coordinator.GetCurrentAsync(default);
        Assert.Equal(ClientShellState.Offline, state.State);
        Assert.Null(state.UserId);
    }

    [Fact]
    public async Task ForceLogoutFromLockedProjection_ReadsAndBindsTheHiddenServerAuthIdentity()
    {
        using var fixture = new Fixture();
        var expected = fixture.AuthId;

        var state = await fixture.Service.ForceLogoutAsync(default);

        Assert.Equal(expected, Assert.Single(fixture.LogoutSessionIds));
        Assert.False(fixture.Authenticated);
        Assert.Equal(ClientShellState.Locked, state.State);
        Assert.Null(state.PlayerAuthSessionId);
    }

    [Fact]
    public async Task LogoutAfterTransferWithNoCurrentPlayer_PerformsNoStationWidePost()
    {
        using var fixture = new Fixture { Authenticated = false, Gaming = null };
        await fixture.PrepareAvailableAsync();

        var outcome = await fixture.Service.LogoutAsync(new PlayerLogoutRequest(Guid.NewGuid()), default);

        Assert.True(outcome.Result.Success);
        Assert.Empty(fixture.LogoutSessionIds);
        Assert.Equal(ClientShellState.Available, outcome.State!.State);
    }

    [Fact]
    public async Task LoginCompensation_BindsLogoutToTheAuthIdentityReturnedByThatLogin()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAvailableAsync();
        var loginAuthId = fixture.AuthId;
        fixture.AfterLoginRead = () => fixture.Coordinator.SetStateAsync(ClientShellState.Locked, null, default);

        var outcome = await fixture.Service.LoginAsync(
            new PlayerLoginRequest(Guid.NewGuid(), "nur", "SyntheticClientTest!234"), default);

        Assert.False(outcome.Result.Success);
        Assert.Equal("STATION_UNAVAILABLE", outcome.Result.ErrorCode);
        Assert.Equal(loginAuthId, Assert.Single(fixture.LogoutSessionIds));
        Assert.False(fixture.Authenticated);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly byte[] _secret = new byte[32];
        public readonly Guid StationId = Guid.NewGuid();
        public Guid UserId { get; set; }
        public Guid AuthId { get; set; } = Guid.NewGuid();
        private readonly DateTime _now = DateTime.UtcNow;
        public MemoryBaseStore BaseStore { get; } = new();
        public MemoryPlayerStore PlayerStore { get; } = new();
        public ClientStateCoordinator Coordinator { get; }
        public PlayerLoginService Service { get; }
        public GamingSessionSnapshot? Gaming { get; set; }
        public bool Authenticated { get; set; } = true;
        public bool ServerUnavailable { get; set; }
        public Action? AfterGamingRead { get; set; }
        public Action? BeforeLogout { get; set; }
        public Func<Task>? AfterLoginRead { get; set; }
        public List<Guid> LogoutSessionIds { get; } = [];
        public List<string> Paths { get; } = [];
        public int ValidHmacRequests { get; private set; }

        public Fixture(Guid? userId = null)
        {
            UserId = userId ?? Guid.NewGuid();
            Gaming = new GamingSessionSnapshot(Guid.NewGuid(), UserId, StationId, "Active",
                _now, _now.AddMinutes(30), 1800, _now, 0);
            Coordinator = new ClientStateCoordinator(BaseStore, PlayerStore,
                Options.Create(new StationOptions { Name = "PC-01" }), TimeProvider.System);
            var credential = new StationCredentialData(StationId,
                SecurityEncoding.ToBase64Url(_secret), "unused", "ES256");
            _http = new HttpClient(new Handler(HandleAsync));
            var api = new StationApiClient(_http,
                Options.Create(new ServerOptions { BaseUrl = "https://localhost:5001" }),
                Options.Create(new SecurityOptions()), new TestEnvironment(), TimeProvider.System);
            Service = new PlayerLoginService(api, new CredentialStore(credential), Coordinator,
                NullLogger<PlayerLoginService>.Instance);
        }

        public async Task PrepareAvailableAsync()
        {
            await Coordinator.CompleteSessionReconciliationAsync(null, default);
            await Coordinator.SetStateAsync(ClientShellState.Available, null, default);
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ServerUnavailable)
            {
                throw new HttpRequestException("Synthetic server unavailable");
            }

            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(StationId.ToString("D"), request.Headers.GetValues(
                HmacRequestAuthentication.StationIdHeader).Single());
            var signature = HmacRequestAuthentication.Sign(_secret, request.Method.Method, path,
                request.Headers.GetValues(HmacRequestAuthentication.TimestampHeader).Single(),
                request.Headers.GetValues(HmacRequestAuthentication.NonceHeader).Single(), body);
            Assert.Equal(signature, request.Headers.GetValues(HmacRequestAuthentication.SignatureHeader).Single());
            ValidHmacRequests++;
            if (path == "/api/agent/player/logout")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var payload = System.Text.Json.JsonDocument.Parse(body);
                var expected = payload.RootElement.GetProperty("expectedSessionId").GetGuid();
                Assert.NotEqual(Guid.Empty, expected);
                LogoutSessionIds.Add(expected);
                BeforeLogout?.Invoke();
                if (expected == AuthId)
                {
                    Authenticated = false;
                    Gaming = null;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { success = true }) };
            }

            if (path == "/api/agent/player/login")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PlayerSessionApiResponse(true, AuthId,
                        _now, _now.AddHours(12), new PlayerApiUser(UserId, "nur", "Nur")))
                };
                if (AfterLoginRead is not null) await AfterLoginRead();
                return response;
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            if (path == "/api/agent/gaming/session/current")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new StationGamingState(Gaming, _now)) };
                AfterGamingRead?.Invoke();
                return response;
            }

            Assert.Equal("/api/agent/player/session/current", path);
            return !Authenticated
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PlayerSessionApiResponse(true, AuthId,
                        _now, _now.AddHours(12), new PlayerApiUser(UserId, "nur", "Nur")))
                };
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }

    private sealed class MemoryBaseStore : IClientStateStore
    {
        private PersistedClientState? _value;
        public Task<PersistedClientState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_value);
        public Task SaveAsync(ClientShellState state, DateTime updatedAtUtc, CancellationToken cancellationToken)
        { _value = new PersistedClientState(state, updatedAtUtc); return Task.CompletedTask; }
    }

    private sealed class MemoryPlayerStore : IPlayerSessionStateStore
    {
        public PersistedPlayerSession? Value { get; private set; }
        public Task<PersistedPlayerSession?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public Task SaveAsync(PersistedPlayerSession session, CancellationToken cancellationToken)
        { Value = session; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken) { Value = null; return Task.CompletedTask; }
    }

    private sealed class CredentialStore(StationCredentialData credential) : IStationCredentialStore
    {
        public Task<StationCredentialData?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<StationCredentialData?>(credential);
        public Task SaveAsync(StationCredentialData value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "GameClub.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
