using GameClub.Agent.Configuration;
using GameClub.Agent.Services.Client;
using GameClub.Contracts.Client;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Client;

public sealed class ClientStatePersistenceTests
{
    [Fact]
    public async Task LockedState_PersistsAfterAgentRestart()
    {
        var path = GetDatabasePath();
        try
        {
            var first = CreateCoordinator(path);
            await first.CompleteSessionReconciliationAsync(null, default);
            await first.SetStateAsync(ClientShellState.Locked, null, default);

            var restarted = CreateCoordinator(path);
            await restarted.CompleteSessionReconciliationAsync(null, default);
            var restored = await restarted.GetCurrentAsync(default);

            Assert.Equal(ClientShellState.Locked, restored.State);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ClientReconnect_GetsCurrentPersistedState()
    {
        var path = GetDatabasePath();
        try
        {
            var coordinator = CreateCoordinator(path);
            await coordinator.CompleteSessionReconciliationAsync(null, default);
            await coordinator.SetStateAsync(ClientShellState.Available, null, default);
            coordinator.MarkClientConnected();
            coordinator.MarkClientDisconnected();
            coordinator.MarkClientConnected();

            var current = await coordinator.GetCurrentAsync(default);

            Assert.True(coordinator.ClientConnected);
            Assert.NotNull(coordinator.ClientLastSeenAtUtc);
            Assert.Equal(ClientShellState.Available, current.State);
            Assert.Equal("PC-01", current.StationName);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task PlayerSessionState_PersistsAndRequiresServerReconciliationAfterRestart()
    {
        var path = GetDatabasePath();
        try
        {
            var session = new PersistedPlayerSession(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "nur",
                "Nur",
                DateTime.UtcNow,
                DateTime.UtcNow.AddHours(12));
            var first = CreateCoordinator(path);
            await first.CompleteSessionReconciliationAsync(null, default);
            await first.SetStateAsync(ClientShellState.Available, null, default);
            var active = await first.ActivatePlayerSessionAsync(session, default);
            Assert.Equal(ClientShellState.SessionActive, active.State);

            var restarted = CreateCoordinator(path);
            var beforeValidation = await restarted.GetCurrentAsync(default);
            var restored = await new SqlitePlayerSessionStateStore(
                    Options.Create(new SecurityOptions { ProcessedCommandStorePath = path }))
                .LoadAsync(default);
            var afterValidation = await restarted.CompleteSessionReconciliationAsync(restored, default);

            Assert.Equal(ClientShellState.Offline, beforeValidation.State);
            Assert.NotNull(restored);
            Assert.Equal(session.SessionId, restored.SessionId);
            Assert.Equal(ClientShellState.SessionActive, afterValidation.State);
            Assert.Equal(session.UserId, afterValidation.UserId);
            Assert.Equal("Nur", afterValidation.DisplayName);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static ClientStateCoordinator CreateCoordinator(string path)
    {
        var security = Options.Create(new SecurityOptions { ProcessedCommandStorePath = path });
        var station = Options.Create(new StationOptions { Name = "PC-01" });
        return new ClientStateCoordinator(
            new SqliteClientStateStore(security),
            new SqlitePlayerSessionStateStore(security),
            station,
            TimeProvider.System);
    }

    private static string GetDatabasePath() =>
        Path.Combine(AppContext.BaseDirectory, $"client-state-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
