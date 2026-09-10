using System.Security.Cryptography;
using GameClub.Agent.Models;
using GameClub.Agent.Security;
using GameClub.Agent.Services.Commands;
using GameClub.Agent.Services.Client;
using GameClub.Contracts.Client;
using GameClub.Domain.Commands;
using GameClub.Domain.Security;
using GameClub.Agent.Services.Players;
using Microsoft.Extensions.Logging;
using Xunit;
using Microsoft.Extensions.Options;
using GameClub.Agent.Services.Games;
using GameClub.Contracts.Games;
using GameClub.Contracts.Gaming;
using System.Net.Http.Json;
using GameClub.Agent.Services;
using GameClub.Agent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;

namespace GameClub.Server.Tests.Commands;

public sealed class AgentCommandHandlerTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _stationId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("{\"gameId\":\"00000000-0000-0000-0000-000000000001\",\"executable\":\"cmd.exe\"}")]
    [InlineData("{\"gameId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"gameId\":null,\"gamingSessionId\":null,\"playniteGameId\":null}")]
    public async Task SignedLaunchGame_InvalidOrArbitraryPathPayload_IsNotAcknowledged(string payload)
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(CreateSignedCommand("LaunchGame", payload,
            expiresAtUtc: _now.UtcDateTime.AddSeconds(30)), fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task LaunchGame_TtlOverThirtySeconds_IsRejected()
    {
        var fixture = CreateFixture();
        var payload = System.Text.Json.JsonSerializer.Serialize(new LaunchGamePayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await fixture.Handler.HandleAsync(CreateSignedCommand("LaunchGame", payload,
            expiresAtUtc: _now.UtcDateTime.AddSeconds(31)), fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task LaunchGame_OtherwiseValidPayloadWithExtraExecutable_IsRejected()
    {
        var fixture = CreateFixture();
        var payload = System.Text.Json.JsonSerializer.Serialize(new { gameId = Guid.NewGuid(),
            gamingSessionId = Guid.NewGuid(), playniteGameId = Guid.NewGuid(), executable = "cmd.exe" });
        await fixture.Handler.HandleAsync(CreateSignedCommand("LaunchGame", payload,
            expiresAtUtc: _now.UtcDateTime.AddSeconds(30)), fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task LaunchGame_HandsOffTypedActionOnce_DuplicateUsesLedger()
    {
        var bridge = new FakeGameBridge();
        var fixture = CreateFixture(games: bridge, playnite: bridge);
        var payload = new LaunchGamePayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var command = CreateSignedCommand("LaunchGame", System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            expiresAtUtc: _now.UtcDateTime.AddSeconds(30));
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);
        Assert.Equal(1, bridge.Prepared);
        Assert.Equal(1, bridge.Executed);
        Assert.Equal(payload.GameId, bridge.Last!.GameId);
        Assert.Equal(payload.GamingSessionId, bridge.Last.GamingSessionId);
        Assert.Equal(1, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(2, fixture.Reporter.CompleteCount);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("no-session")]
    [InlineData("mismatch")]
    [InlineData("lost-state")]
    [InlineData("allowlist-denied")]
    [InlineData("valid")]
    public async Task GamePreparation_FailsClosedAndChecksLocalAllowlist(string scenario)
    {
        var fixture = CreateFixture();
        var userId = Guid.NewGuid();
        var gameId = Guid.NewGuid();
        var playniteId = Guid.NewGuid();
        var snapshot = new GamingSessionSnapshot(Guid.NewGuid(), userId, _stationId, "Active", _now.UtcDateTime,
            _now.UtcDateTime.AddHours(1), 3600, _now.UtcDateTime, 0, Guid.NewGuid());
        fixture.ClientState.Override = new ClientStateMessage(ClientShellState.SessionActive, "PC", _now.UtcDateTime, null, 1,
            userId, "player", null, Guid.NewGuid(), snapshot);
        if (scenario == "paused") fixture.ClientState.Override = fixture.ClientState.Override with { GamingSession = snapshot with { Status = "Paused" } };
        if (scenario == "no-session") fixture.ClientState.Override = fixture.ClientState.Override with { GamingSession = null };
        var calls = 0;
        using var http = new HttpClient(new GameHttpHandler(() =>
        {
            calls++;
            if (scenario == "lost-state") fixture.ClientState.Override = fixture.ClientState.Override with { State = ClientShellState.Offline };
            return new GameAuthorizationResponse(scenario == "mismatch" ? Guid.NewGuid() : snapshot.Id, gameId,
                playniteId, _now.UtcDateTime.AddSeconds(10));
        }));
        var library = new FakeLibrary { Deny = scenario == "allowlist-denied" };
        var api = new StationApiClient(http, Options.Create(new ServerOptions { BaseUrl = "https://localhost:5001" }),
            Options.Create(new SecurityOptions()), new GameEnvironment(), fixture.Clock);
        var credential = new StationCredentialData(_stationId, SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(32)), "unused", "ES256");
        var service = new AgentGameLaunchService(library, api, new FakeCredentialStore(credential), fixture.ClientState,
            fixture.PlayerLogin, fixture.Clock);
        if (scenario == "valid")
        {
            var action = await service.PrepareAsync(Guid.NewGuid(), gameId, snapshot.Id, playniteId,
                _now.UtcDateTime.AddSeconds(5), default);
            Assert.Equal(PlayniteActionType.LaunchGame, action.Action);
            Assert.Equal(gameId, action.GameId);
            Assert.Equal(_now.UtcDateTime.AddSeconds(5), action.ExpiresAtUtc);
            Assert.Equal((gameId, playniteId), library.Last);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(Guid.NewGuid(), gameId,
            snapshot.Id, playniteId, _now.UtcDateTime.AddSeconds(30), default));
        Assert.Equal(scenario is "paused" or "no-session" ? 0 : 1, calls);
    }

    [Fact]
    public async Task ValidSignedCommand_IsAcknowledgedAndCompleted()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand();

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(1, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(1, fixture.Reporter.CompleteCount);
        Assert.Equal(0, fixture.Reporter.FailCount);
    }

    [Fact]
    public async Task InvalidCommandSignature_IsNotAcknowledged()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand() with { Signature = "invalid" };

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Contains(fixture.Logger.Messages, value => value.Contains("invalid signature"));
    }

    [Fact]
    public async Task PayloadTampering_IsNotAcknowledged()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(
            nameof(GameClub.Agent.Models.AgentCommandType.TestMessage),
            "{\"message\":\"safe\"}") with
        {
            PayloadJson = "{\"message\":\"tampered\"}"
        };

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task CommandForWrongStation_IsNotAcknowledged()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(stationId: Guid.NewGuid());

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task ExpiredCommand_IsRejectedBeforeAcknowledgement()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(expiresAtUtc: _now.UtcDateTime.AddSeconds(-1));

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(1, fixture.Reporter.FailCount);
    }

    [Fact]
    public async Task DuplicateCompletedCommand_IsNotExecutedTwice()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand();

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);
        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(1, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(2, fixture.Reporter.CompleteCount);
        Assert.Single(fixture.Logger.Messages, value => value.Contains("Received Ping command"));
    }

    [Fact]
    public async Task LockCommand_ChangesClientStateAfterAcknowledgement()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(nameof(GameClub.Agent.Models.AgentCommandType.LockStation));

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(ClientShellState.Locked, fixture.ClientState.CurrentState);
        Assert.Equal(1, fixture.ClientState.UpdateCount);
        Assert.Equal(1, fixture.Reporter.CompleteCount);
    }

    [Fact]
    public async Task UnlockCommand_ChangesClientStateToAvailable()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(nameof(GameClub.Agent.Models.AgentCommandType.UnlockStation));

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(ClientShellState.Available, fixture.ClientState.CurrentState);
        Assert.Equal(1, fixture.Reporter.CompleteCount);
    }

    [Fact]
    public async Task DuplicateLockCommand_DoesNotChangeStateTwice()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(nameof(GameClub.Agent.Models.AgentCommandType.LockStation));

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);
        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(1, fixture.ClientState.UpdateCount);
        Assert.Equal(2, fixture.Reporter.CompleteCount);
    }

    [Fact]
    public async Task InvalidSignedLock_IsRejectedWithoutStateChange()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(nameof(GameClub.Agent.Models.AgentCommandType.LockStation)) with
        {
            Signature = "invalid"
        };

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(0, fixture.ClientState.UpdateCount);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task LockForWrongStation_IsRejectedWithoutStateChange()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(
            nameof(GameClub.Agent.Models.AgentCommandType.LockStation),
            stationId: Guid.NewGuid());

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(0, fixture.ClientState.UpdateCount);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task LockWithoutConnectedClient_IsPersistedAndCompleted()
    {
        var fixture = CreateFixture(new ClientStateDelivery(false, false));
        var command = CreateSignedCommand(nameof(GameClub.Agent.Models.AgentCommandType.LockStation));

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(ClientShellState.Locked, fixture.ClientState.CurrentState);
        Assert.Equal(1, fixture.Reporter.CompleteCount);
        Assert.Equal(0, fixture.Reporter.FailCount);
    }

    [Fact]
    public async Task LogoutPlayerCommand_UsesServerConfirmedLogoutAndCompletes()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(nameof(GameClub.Agent.Models.AgentCommandType.LogoutPlayer));

        await fixture.Handler.HandleAsync(command, fixture.Reporter, CancellationToken.None);

        Assert.Equal(1, fixture.PlayerLogin.ForceLogoutCount);
        Assert.Equal(ClientShellState.Available, fixture.ClientState.CurrentState);
        Assert.Equal(1, fixture.Reporter.CompleteCount);
    }

    [Theory]
    [InlineData("RestartStation")]
    [InlineData("ShutdownStation")]
    public async Task PowerCommand_UsesTypedFakeOnce_AfterVerifiedAcknowledgement(string type)
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand(type);
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);

        Assert.Equal(type == "RestartStation" ? 1 : 0, fixture.Power.Restarts);
        Assert.Equal(type == "ShutdownStation" ? 1 : 0, fixture.Power.Shutdowns);
        Assert.Equal(1, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(2, fixture.Reporter.CompleteCount);
        Assert.Equal(0, fixture.ClientState.UpdateCount); // No persisted Maintenance or other base-state mutation.
    }

    [Theory]
    [InlineData("RestartStation", "{}")]
    [InlineData("RestartStation", "{\"delaySeconds\":0}")]
    [InlineData("ShutdownStation", "{\"force\":true}")]
    [InlineData("ShutdownStation", "\"shutdown.exe\"")]
    public async Task PowerPayload_IsRejectedBeforeAcknowledgement(string type, string payload)
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(CreateSignedCommand(type, payload), fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(0, fixture.Power.Restarts + fixture.Power.Shutdowns);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("restartstation")]
    [InlineData("shutdown.exe")]
    public async Task NumericOrNonAllowlistedPowerType_IsRejected(string type)
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(CreateSignedCommand(type), fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(0, fixture.Power.Restarts + fixture.Power.Shutdowns);
    }

    [Fact]
    public async Task InvalidPowerSignature_IsRejectedWithoutPowerRequest()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(CreateSignedCommand("RestartStation") with { Signature = "invalid" }, fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(0, fixture.Power.Restarts);
    }

    [Fact]
    public async Task PowerForAnotherStation_IsRejectedWithoutPowerRequest()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(CreateSignedCommand("RestartStation", stationId: Guid.NewGuid()), fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(0, fixture.Power.Restarts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task ExpiredOrExcessivelyLongPowerCommand_CannotExecute(int seconds)
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(CreateSignedCommand("RestartStation", expiresAtUtc: _now.UtcDateTime.AddSeconds(seconds)),
            fixture.Reporter, default);
        Assert.Equal(0, fixture.Reporter.AcknowledgeCount);
        Assert.Equal(0, fixture.Power.Restarts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveOrUnreconciledClient_BlocksPowerWithoutChangingBaseState(bool active)
    {
        var fixture = CreateFixture();
        fixture.ClientState.CurrentState = active ? ClientShellState.SessionActive : ClientShellState.Available;
        fixture.ClientState.SessionReconciliationCompleted = active;
        await fixture.Handler.HandleAsync(CreateSignedCommand("ShutdownStation"), fixture.Reporter, default);
        Assert.Equal(1, fixture.Reporter.FailCount);
        Assert.Equal(0, fixture.Power.Shutdowns);
        Assert.Equal(0, fixture.ClientState.UpdateCount);
    }

    [Fact]
    public async Task PowerExpiryWhileWaitingForAck_BlocksNativeRequest()
    {
        var fixture = CreateFixture();
        fixture.Reporter.OnAcknowledged = () => fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await fixture.Handler.HandleAsync(CreateSignedCommand("RestartStation"), fixture.Reporter, default);
        Assert.Equal(1, fixture.Reporter.FailCount);
        Assert.Equal(0, fixture.Power.Restarts);
    }

    [Fact]
    public async Task UnsupportedOperatingSystem_IsFailedWithoutSchedulingPower()
    {
        var fixture = CreateFixture();
        fixture.Power.Unsupported = true;
        await fixture.Handler.HandleAsync(CreateSignedCommand("ShutdownStation"), fixture.Reporter, default);
        Assert.Equal(1, fixture.Reporter.FailCount);
        Assert.Equal(0, fixture.Power.Shutdowns);
    }

    [Fact]
    public async Task AcceptedPower_WhenCompletionReportFails_DoesNotFailOrExecuteAgain()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand("RestartStation");
        fixture.Reporter.RejectCompletion = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Handler.HandleAsync(command, fixture.Reporter, default));
        Assert.Equal(0, fixture.Reporter.FailCount);
        fixture.Reporter.RejectCompletion = false;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1)); // Retried after TTL: report success, do not send Failed.
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);
        Assert.Equal(1, fixture.Power.Restarts);
        Assert.Equal(2, fixture.Reporter.CompleteCount);
        Assert.Equal(0, fixture.Reporter.FailCount);
    }

    [Fact]
    public async Task AcceptedPower_WhenCompletionPersistenceFails_KeepsAcknowledgedLedgerAndLease()
    {
        var store = new InMemoryProcessedCommandStore();
        var first = CreateFixture(store: new RejectCompletedStore(store));
        var command = CreateSignedCommand("ShutdownStation");
        await Assert.ThrowsAsync<IOException>(() => first.Handler.HandleAsync(command, first.Reporter, default));
        Assert.True(store.TryGet(command.CommandId, out var state));
        Assert.Equal(ProcessedCommandStatus.Acknowledged, state.Status);
        Assert.Equal(0, first.Reporter.FailCount);
        var restarted = CreateFixture(store: store, power: first.Power);
        await restarted.Handler.HandleAsync(command, restarted.Reporter, default);
        Assert.Equal(1, first.Power.Shutdowns);
        Assert.Equal(1, restarted.Reporter.AcknowledgeCount);
        Assert.Equal(0, restarted.Reporter.CompleteCount);
    }

    [Fact]
    public async Task PersistentPowerReplay_AfterAgentRestart_DoesNotCallNativeTwice()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gameclub_power_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "commands.db");
        try
        {
            var options = Options.Create(new GameClub.Agent.Configuration.SecurityOptions { ProcessedCommandStorePath = path });
            var clock = new FixedTimeProvider(_now);
            var first = CreateFixture(store: new SqliteProcessedCommandStore(options, clock), clock: clock);
            var command = CreateSignedCommand("RestartStation");
            await first.Handler.HandleAsync(command, first.Reporter, default);
            clock.Advance(TimeSpan.FromMinutes(1));
            var restarted = CreateFixture(store: new SqliteProcessedCommandStore(options, clock), power: first.Power, clock: clock);
            await restarted.Handler.HandleAsync(command, restarted.Reporter, default);
            Assert.Equal(1, first.Power.Restarts);
            Assert.Equal(1, restarted.Reporter.CompleteCount);
            Assert.Equal(0, restarted.Reporter.FailCount);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory); // Unique test directory, non-recursive; no user files can be removed.
        }
    }

    [Theory]
    [InlineData(ProcessedCommandStatus.Received)]
    [InlineData(ProcessedCommandStatus.Acknowledged)]
    public async Task PowerInterruptedBeforeResult_IsNeverReexecuted(ProcessedCommandStatus state)
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand("RestartStation");
        fixture.Store.TryBegin(command.CommandId, command.Nonce);
        fixture.Store.Set(command.CommandId, state);
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);
        Assert.Equal(0, fixture.Power.Restarts);
        Assert.Equal(1, fixture.Reporter.AcknowledgeCount);
    }

    [Fact]
    public async Task PowerReusedNonceWithDifferentId_IsRejected()
    {
        var fixture = CreateFixture();
        var command = CreateSignedCommand("RestartStation");
        await fixture.Handler.HandleAsync(command, fixture.Reporter, default);
        var reused = CreateSignedCommand("RestartStation") with { Nonce = command.Nonce };
        reused = reused with { Signature = CommandEnvelopeCryptography.Sign(_key, reused) };
        await fixture.Handler.HandleAsync(reused, fixture.Reporter, default);
        Assert.Equal(1, fixture.Power.Restarts);
        Assert.Equal(1, fixture.Reporter.AcknowledgeCount);
    }

    public void Dispose() => _key.Dispose();

    private sealed class RejectCompletedStore(IProcessedCommandStore inner) : IProcessedCommandStore
    {
        public bool TryBegin(Guid id, string nonce) => inner.TryBegin(id, nonce);
        public bool TryGet(Guid id, out ProcessedCommandState state) => inner.TryGet(id, out state!);
        public void Set(Guid id, ProcessedCommandStatus status, string? error = null)
        {
            if (status == ProcessedCommandStatus.Completed) throw new IOException("Test disk write failure.");
            inner.Set(id, status, error);
        }
        public void Remove(Guid id) => inner.Remove(id);
    }

    private Fixture CreateFixture(ClientStateDelivery? delivery = null, IProcessedCommandStore? store = null,
        FakeSystemPower? power = null, FixedTimeProvider? clock = null,
        IAgentGameLaunchService? games = null, IPlayniteActionTransport? playnite = null)
    {
        var credential = new StationCredentialData(
            _stationId,
            SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
            CommandEnvelopeCryptography.SignatureAlgorithm);
        var logger = new CapturingLogger<AgentCommandHandler>();
        var reporter = new FakeReporter();
        var clientState = new FakeClientStateCoordinator(_now.UtcDateTime);
        var playerLogin = new FakePlayerLoginService(clientState);
        power ??= new FakeSystemPower();
        clock ??= new FixedTimeProvider(_now);
        store ??= new InMemoryProcessedCommandStore();
        var handler = new AgentCommandHandler(
            store,
            new FakeCredentialStore(credential),
            new ServerCommandSignatureVerifier(),
            clientState,
            new FakeClientStateNotifier(delivery ?? new ClientStateDelivery(true, true)),
            playerLogin,
            new RestartStationCommandHandler(power),
            new ShutdownStationCommandHandler(power),
            clock,
            logger, games, playnite);
        return new Fixture(handler, reporter, logger, clientState, playerLogin, power, store, clock);
    }

    private SignedAgentCommandEnvelope CreateSignedCommand(
        string type = nameof(GameClub.Agent.Models.AgentCommandType.Ping),
        string? payload = null,
        Guid? stationId = null,
        DateTime? expiresAtUtc = null)
    {
        var unsigned = new SignedAgentCommandEnvelope(
            Guid.NewGuid(),
            stationId ?? _stationId,
            type,
            payload,
            _now.UtcDateTime,
            expiresAtUtc ?? _now.UtcDateTime.Add(type is "RestartStation" or "ShutdownStation"
                ? StationPowerPolicy.CommandLifetime : TimeSpan.FromMinutes(5)),
            SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(16)),
            string.Empty,
            CommandEnvelopeCryptography.SignatureAlgorithm);
        return unsigned with { Signature = CommandEnvelopeCryptography.Sign(_key, unsigned) };
    }

    private sealed record Fixture(
        AgentCommandHandler Handler,
        FakeReporter Reporter,
        CapturingLogger<AgentCommandHandler> Logger,
        FakeClientStateCoordinator ClientState,
        FakePlayerLoginService PlayerLogin,
        FakeSystemPower Power,
        IProcessedCommandStore Store,
        FixedTimeProvider Clock);

    private sealed class FakeClientStateCoordinator(DateTime timestampUtc) : IClientStateCoordinator
    {
        public ClientStateMessage? Override { get; set; }
        public ClientShellState CurrentState { get; set; } = ClientShellState.Available;
        public int UpdateCount { get; private set; }
        public bool ClientConnected => true;
        public DateTime? ClientLastSeenAtUtc => timestampUtc;
        public bool SessionReconciliationCompleted { get; set; } = true;

        public Task<ClientStateMessage> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Override ?? new ClientStateMessage(CurrentState, "PC-01", timestampUtc, null, UpdateCount));

        public Task<ClientStateMessage> SetStateAsync(
            ClientShellState state,
            string? message,
            CancellationToken cancellationToken)
        {
            CurrentState = state;
            UpdateCount++;
            return Task.FromResult(new ClientStateMessage(state, "PC-01", timestampUtc, message, UpdateCount));
        }

        public Task<ClientStateMessage> ActivatePlayerSessionAsync(
            PersistedPlayerSession session,
            CancellationToken cancellationToken)
        {
            CurrentState = ClientShellState.SessionActive;
            UpdateCount++;
            return Task.FromResult(new ClientStateMessage(
                CurrentState,
                "PC-01",
                timestampUtc,
                null,
                UpdateCount,
                session.UserId,
                session.Username,
                session.DisplayName,
                session.SessionId));
        }

        public Task<ClientStateMessage> CompleteSessionReconciliationAsync(
            PersistedPlayerSession? session,
            CancellationToken cancellationToken) =>
            session is null
                ? ClearPlayerSessionAsync(cancellationToken)
                : ActivatePlayerSessionAsync(session, cancellationToken);

        public Task<ClientStateMessage> ClearPlayerSessionAsync(CancellationToken cancellationToken)
        {
            CurrentState = ClientShellState.Available;
            UpdateCount++;
            return Task.FromResult(new ClientStateMessage(
                CurrentState,
                "PC-01",
                timestampUtc,
                null,
                UpdateCount));
        }

        public Task<ClientStateMessage> CompleteSessionReconciliationAsync(
            PersistedPlayerSession? session,
            GameClub.Contracts.Gaming.GamingSessionSnapshot? gamingSession,
            CancellationToken cancellationToken) => CompleteSessionReconciliationAsync(session, cancellationToken);

        public Task<ClientStateMessage> MarkServerUnavailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClientStateMessage(ClientShellState.Offline, "PC-01", timestampUtc, null, ++UpdateCount));

        public void MarkClientConnected()
        {
        }

        public void MarkClientHeartbeat()
        {
        }

        public void MarkClientDisconnected()
        {
        }
    }

    private sealed class FakePlayerLoginService(FakeClientStateCoordinator clientState)
        : IPlayerLoginService
    {
        public int ForceLogoutCount { get; private set; }

        public Task<AgentPlayerLoginOutcome> LoginAsync(
            PlayerLoginRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentPlayerLogoutOutcome> LogoutAsync(
            PlayerLogoutRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientStateMessage> ReconcileCurrentSessionAsync(
            CancellationToken cancellationToken) => clientState.GetCurrentAsync(cancellationToken);

        public Task<ClientStateMessage> ForceLogoutAsync(CancellationToken cancellationToken)
        {
            ForceLogoutCount++;
            return clientState.ClearPlayerSessionAsync(cancellationToken);
        }
    }

    private sealed class FakeClientStateNotifier(ClientStateDelivery delivery) : IClientStateNotifier
    {
        public Task<ClientStateDelivery> PublishAsync(
            ClientStateMessage state,
            bool waitForAcknowledgement,
            CancellationToken cancellationToken) =>
            Task.FromResult(delivery);
    }

    private sealed class FakeCredentialStore(StationCredentialData credential) : IStationCredentialStore
    {
        public Task<StationCredentialData?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<StationCredentialData?>(credential);

        public Task SaveAsync(StationCredentialData value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeReporter : IAgentCommandReporter
    {
        public int AcknowledgeCount { get; private set; }
        public int CompleteCount { get; private set; }
        public int FailCount { get; private set; }
        public bool RejectCompletion { get; set; }
        public Action? OnAcknowledged { get; set; }

        public Task AcknowledgeAsync(Guid commandId, CancellationToken cancellationToken)
        {
            AcknowledgeCount++;
            OnAcknowledged?.Invoke();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(Guid commandId, CancellationToken cancellationToken)
        {
            CompleteCount++;
            if (RejectCompletion) throw new IOException("Test completion connection lost.");
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid commandId, string error, CancellationToken cancellationToken)
        {
            FailCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan amount) => utcNow += amount;
    }

    private sealed class FakeSystemPower : ISystemPowerService
    {
        public int Restarts { get; private set; }
        public int Shutdowns { get; private set; }
        public bool Unsupported { get; set; }
        public void ScheduleRestart() { if (Unsupported) throw new PlatformNotSupportedException(); Restarts++; }
        public void ScheduleShutdown() { if (Unsupported) throw new PlatformNotSupportedException(); Shutdowns++; }
    }

    private sealed class FakeGameBridge : IAgentGameLaunchService, IPlayniteActionTransport
    {
        public int Prepared { get; private set; }
        public int Executed { get; private set; }
        public PlayniteActionRequest? Last { get; private set; }
        public Task<PlayniteActionRequest> PrepareAsync(Guid requestId, Guid? gameId, Guid? expectedSessionId,
            Guid? expectedPlayniteId, DateTime? commandExpiry, CancellationToken ct)
        {
            Prepared++;
            return Task.FromResult(new PlayniteActionRequest(requestId, PlayniteActionType.LaunchGame,
                expectedSessionId!.Value, commandExpiry!.Value, gameId, expectedPlayniteId));
        }
        public Task<PlayniteActionResult> ExecutePlayniteAsync(PlayniteActionRequest request, CancellationToken ct)
        {
            Executed++;
            Last = request;
            return Task.FromResult(new PlayniteActionResult(request.RequestId, true, null));
        }
    }

    private sealed class FakeLibrary : ILocalPlayniteLibrary
    {
        public bool Deny { get; init; }
        public (Guid, Guid)? Last { get; private set; }
        public Task<IReadOnlyList<StationGameInventoryEntry>> ReadInventoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RequireAvailableAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RequireInstalledAsync(Guid gameId, Guid playniteId, CancellationToken ct)
        {
            Last = (gameId, playniteId);
            if (Deny) throw new InvalidOperationException("GAME_NOT_INSTALLED");
            return Task.CompletedTask;
        }
    }

    private sealed class GameHttpHandler(Func<GameAuthorizationResponse> grant) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(grant()) });
    }

    private sealed class GameEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "GameTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
