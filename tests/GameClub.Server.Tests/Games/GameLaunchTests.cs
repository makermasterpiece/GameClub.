using GameClub.Application.Abstractions;
using GameClub.Application.Games;
using GameClub.Domain.Billing;
using GameClub.Domain.Games;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Tests.Integration;
using Xunit;

namespace GameClub.Server.Tests.Games;

[Collection(PostgresCollection.Name)]
public sealed class GameLaunchTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task FullscreenBootstrapsWithoutInventory_IndividualLaunchRequiresFreshInstalledGame()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var now = DateTime.UtcNow;
        var clock = new Clock(now);
        var station = new Station(Guid.NewGuid(), "PC", "PC", null, "test", now);
        station.UpdateClientHealth(true, "SessionActive", now, now);
        var user = new User(Guid.NewGuid(), "games-player", null, null, null, now);
        var tariff = new Tariff(Guid.NewGuid(), "Games", station.StationGroupId!.Value, 60m);
        var session = new GamingSession(Guid.NewGuid(), user.Id, station.Id, null, now);
        session.SetPricing(tariff.Id, null, 0m);
        session.ConfigureBilling(BillingMode.Postpaid, 60m, null, 3600, new string('A', 64));
        session.Start(now);
        var game = new Game(Guid.NewGuid(), "Game", Guid.NewGuid().ToString(), null, null);
        db.AddRange(station, user, tariff, session, game,
            new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, now, now.AddHours(1)));
        await db.SaveChangesAsync();
        var service = new GameLaunchService(new ClubData(db), null!, clock);
        var menu = await service.ValidateCoreAsync(station.Id, session.Id, null, default);
        Assert.Null(menu.PlayniteGameId);
        Assert.Equal(now.AddSeconds(10), menu.ExpiresAtUtc);
        var denied = await Assert.ThrowsAsync<ClubException>(() => service.ValidateCoreAsync(station.Id, session.Id, game.Id, default));
        Assert.Equal("GAME_UNAVAILABLE", denied.Code);
        var report = new StationGame(station.Id, game.Id, true, now.AddSeconds(-91));
        db.Add(report);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ClubException>(() => service.ValidateCoreAsync(station.Id, session.Id, game.Id, default));
        report.Report(true, now);
        await db.SaveChangesAsync();
        var grant = await service.ValidateCoreAsync(station.Id, session.Id, game.Id, default);
        Assert.Equal(Guid.Parse(game.PlayniteGameId!), grant.PlayniteGameId);
        session.Pause(now);
        await db.SaveChangesAsync();
        var paused = await Assert.ThrowsAsync<ClubException>(() => service.ValidateCoreAsync(station.Id, session.Id, null, default));
        Assert.Equal("ACTIVE_PAID_SESSION_REQUIRED", paused.Code);
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
