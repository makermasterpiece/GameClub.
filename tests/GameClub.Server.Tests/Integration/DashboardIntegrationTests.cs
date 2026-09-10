using System.Text.Json;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Server.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class DashboardIntegrationTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task MigratedPostgres_ProvidesConsistentSafeDashboardProjectionAndPersistedHealth()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var now = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);
        var station = new Station(Guid.NewGuid(), "Dashboard SQL", "SQL-" + Guid.NewGuid().ToString("N"), "127.0.0.1", "0.1.0", now);
        station.UpdateClientHealth(true, "SessionActive", now, now);
        var user = new User(Guid.NewGuid(), "dashboard_sql", "SQL player", "private@example.test", null, now);
        user.SetPasswordHash("private-dashboard-test-hash");
        var session = new GamingSession(Guid.NewGuid(), user.Id, station.Id, 30, now);
        session.Start(now);
        await using (var seed = database.CreateContext())
        {
            seed.AddRange(station, user, session,
                new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, now, now.AddHours(12)));
            await seed.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var response = await new DashboardService(context, new FixedClock(now)).GetAsync(default);
        var row = Assert.Single(response.Stations);
        Assert.Equal(station.Id, row.Id);
        Assert.Equal("STANDARD", row.GroupName);
        Assert.True(row.AgentOnline);
        Assert.True(row.ClientConnected);
        Assert.Equal("SessionActive", row.ClientState);
        Assert.Equal(user.Id, row.CurrentUser?.Id);
        Assert.Equal(session.Id, row.GamingSession?.Id);
        Assert.Equal(1800, row.GamingSession?.RemainingSeconds);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("private-dashboard-test-hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", json, StringComparison.Ordinal);
        Assert.Equal(now, (await context.Stations.AsNoTracking().SingleAsync()).ClientLastSeenAtUtc);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
