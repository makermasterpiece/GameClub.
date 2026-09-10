using System.Text.Json;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Controllers;
using GameClub.Server.Hubs;
using GameClub.Server.Security.Employees;
using GameClub.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Admin;

public sealed class DashboardTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ClientHealth_AcceptsFreshAllowlistedStateAndKeepsDomainIndependent()
    {
        var station = NewStation();
        Assert.True(station.UpdateClientHealth(true, "Available", Now.AddSeconds(-15), Now));
        Assert.True(station.ClientConnected);
        Assert.Equal("Available", station.ClientState);
        Assert.False(station.UpdateClientHealth(true, "Available", Now.AddSeconds(-1), Now));
        Assert.Equal(Now.AddSeconds(-1), station.ClientLastSeenAtUtc);
        Assert.DoesNotContain(typeof(Station).Assembly.GetReferencedAssemblies(), a =>
            a.Name is "GameClub.Contracts" or "GameClub.Infrastructure" or "GameClub.Server" or "GameClub.Agent");
    }

    [Theory]
    [InlineData("ArbitraryClientText", 0)]
    [InlineData("Available", 1)]
    [InlineData("SessionActive", -16)]
    public void ClientHealth_RejectsUnknownFutureOrStaleReports(string state, int seconds)
    {
        var station = NewStation();
        station.UpdateClientHealth(true, state, Now.AddSeconds(seconds), Now);
        Assert.False(station.ClientConnected);
        Assert.Equal("Offline", station.ClientState);
        if (seconds > 0) Assert.Null(station.ClientLastSeenAtUtc);
    }

    [Fact]
    public void ClientHealth_RejectsMissingUnspecifiedOrCorruptTimestamps()
    {
        foreach (var value in new DateTime?[] { null, DateTime.MinValue, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified) })
        {
            var station = NewStation();
            station.UpdateClientHealth(true, "Available", value, Now);
            Assert.False(station.ClientConnected);
            Assert.Null(station.ClientLastSeenAtUtc);
        }
    }

    [Fact]
    public async Task Dashboard_SeparatesAgentOnlineFromDisconnectedOrStaleClient()
    {
        await using var context = CreateContext();
        var clock = new MutableClock();
        var station = NewStation();
        station.UpdateClientHealth(true, "Available", Now, Now);
        context.Stations.Add(station);
        await context.SaveChangesAsync();
        var dashboard = new DashboardService(context, clock);
        var first = Assert.Single((await dashboard.GetAsync(default)).Stations);
        Assert.True(first.AgentOnline);
        Assert.True(first.ClientConnected);
        clock.Advance(TimeSpan.FromSeconds(16));
        var clientStale = Assert.Single((await dashboard.GetAsync(default)).Stations);
        Assert.True(clientStale.AgentOnline);
        Assert.False(clientStale.ClientConnected);
        Assert.Equal("Offline", clientStale.ClientState);
        clock.Advance(TimeSpan.FromSeconds(15));
        var agentStale = Assert.Single((await dashboard.GetAsync(default)).Stations);
        Assert.False(agentStale.AgentOnline);
        Assert.False(agentStale.ClientConnected);
        Assert.Equal("Offline", agentStale.Status);
        // Dashboard remains a read model, not an implicit mutation of persisted station state.
        Assert.Equal(StationStatus.Online, station.Status);
    }

    [Fact]
    public async Task Dashboard_ReturnsSafeUserSessionAndGroupProjectionWithoutCredentialFields()
    {
        await using var context = CreateContext();
        var clock = new MutableClock();
        var station = NewStation();
        var user = NewUser();
        var group = new StationGroup(station.StationGroupId!.Value, "STANDARD");
        var tariff = new Tariff(Guid.NewGuid(), "Standard hour", group.Id, 60m);
        var session = new GamingSession(Guid.NewGuid(), user.Id, station.Id, 30, Now);
        session.SetPricing(tariff.Id, null, 30m);
        session.Start(Now);
        context.AddRange(group, tariff, station, user, session,
            new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, Now, Now.AddHours(12)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        clock.Advance(TimeSpan.FromSeconds(5));

        var response = await new DashboardService(context, clock).GetAsync(default);
        var result = Assert.Single(response.Stations);
        Assert.Equal("STANDARD", result.GroupName);
        Assert.Equal(user.Id, result.CurrentUser?.Id);
        Assert.Equal("Player display", result.CurrentUser?.DisplayName);
        Assert.Equal(session.Id, result.GamingSession?.Id);
        Assert.Equal(1795, result.GamingSession?.RemainingSeconds);
        Assert.Equal("Standard hour", result.TariffName);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, response.ServerTimeUtc);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-hash-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", json, StringComparison.Ordinal);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Dashboard_DoesNotExposeExpiredOrBannedAuthenticationAsCurrentPlayer()
    {
        await using var context = CreateContext();
        var station = NewStation();
        var user = NewUser();
        var auth = new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, Now.AddHours(-2), Now.AddHours(-1));
        var session = new GamingSession(Guid.NewGuid(), user.Id, station.Id, null, Now);
        session.Start(Now);
        context.AddRange(station, user, auth, session);
        await context.SaveChangesAsync();
        var service = new DashboardService(context, new MutableClock());
        var expired = Assert.Single((await service.GetAsync(default)).Stations);
        Assert.Null(expired.CurrentUser);
        Assert.Null(expired.GamingSession);
        context.PlayerAuthSessions.Remove(auth);
        context.PlayerAuthSessions.Add(new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, Now, Now.AddHours(12)));
        user.ChangeStatus(UserStatus.Banned);
        await context.SaveChangesAsync();
        var banned = Assert.Single((await service.GetAsync(default)).Stations);
        Assert.Null(banned.CurrentUser);
        Assert.Null(banned.GamingSession);
    }

    [Fact]
    public void DashboardFingerprint_IgnoresClockTicksButDetectsMeaningfulChanges()
    {
        var station = new DashboardStation(Guid.NewGuid(), "PC-01", null, null, "Online", true, true,
            "Available", "0.1.0", Now, null,
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Active", Now, Now.AddMinutes(30), 1800, Now, 0), null);
        var original = new DashboardResponse(Now, [station]);
        var ticking = new DashboardResponse(Now.AddSeconds(5), [station with
        {
            LastSeenAtUtc = Now.AddSeconds(5),
            GamingSession = station.GamingSession! with { RemainingSeconds = 1795, ElapsedSeconds = 5, ServerTimeUtc = Now.AddSeconds(5) }
        }]);
        Assert.Equal(DashboardBroadcaster.MeaningfulFingerprint(original), DashboardBroadcaster.MeaningfulFingerprint(ticking));
        Assert.NotEqual(DashboardBroadcaster.MeaningfulFingerprint(original),
            DashboardBroadcaster.MeaningfulFingerprint(new DashboardResponse(Now, [station with { ClientConnected = false, ClientState = "Offline" }])));
        Assert.NotEqual(DashboardBroadcaster.MeaningfulFingerprint(original),
            DashboardBroadcaster.MeaningfulFingerprint(new DashboardResponse(Now, [station with { GamingSession = null }])));
    }

    [Fact]
    public void DashboardAndHub_RequireEmployeeReadPolicyNotAgentJwt()
    {
        foreach (var type in new[] { typeof(AdminDashboardController), typeof(AdminHub) })
        {
            var authorization = Assert.Single(type.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
            Assert.Equal(EmployeeAuthenticationDefaults.Scheme, authorization.AuthenticationSchemes);
            Assert.Equal(EmployeePermissions.Read, authorization.Policy);
        }
    }

    private static GameClubDbContext CreateContext() => new(new DbContextOptionsBuilder<GameClubDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private static Station NewStation() => new(Guid.NewGuid(), "PC-01", "PC-" + Guid.NewGuid().ToString("N"), "127.0.0.1", "0.1.0", Now);
    private static User NewUser()
    {
        var user = new User(Guid.NewGuid(), "dashboard_player", "Player display", "private@example.test", "+12345", Now);
        user.SetPasswordHash("private-hash-sentinel");
        return user;
    }
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(Now);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
