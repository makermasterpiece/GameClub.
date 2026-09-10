using GameClub.Application.Abstractions;
using GameClub.Application.Games;
using GameClub.Contracts.Games;
using GameClub.Domain.Games;
using GameClub.Domain.Stations;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Games;

[Collection(PostgresCollection.Name)]
public sealed class GameCatalogTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("not-a-guid", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData(null, "file:///C:/secret")]
    [InlineData(null, "https://user:password@example.com/cover")]
    [InlineData(null, "javascript:alert(1)")]
    public void UnsafeMetadata_IsRejected(string? playniteId, string? cover) =>
        Assert.Throws<ArgumentException>(() => new Game(Guid.NewGuid(), "Game", playniteId, null, cover));

    [Fact]
    public void Metadata_IsNormalized_AndNeverBecomesAnExecutionInstruction()
    {
        var id = Guid.NewGuid();
        var game = new Game(Guid.NewGuid(), " Example ", id.ToString("B"), " legacy.exe ", null);
        Assert.Equal("Example", game.Name);
        Assert.Equal(id.ToString("D"), game.PlayniteGameId);
        Assert.Equal("legacy.exe", game.Executable);
    }

    [PostgresFact]
    public async Task Inventory_FullReplacementAndCatalogRemapInvalidateInstalledReport()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var station = new Station(Guid.NewGuid(), "PC", "PC", null, "test", DateTime.UtcNow);
        db.Stations.Add(station);
        await db.SaveChangesAsync();
        var catalog = new GameCatalogService(new ClubData(db), TimeProvider.System);
        var playnite = Guid.NewGuid();
        var game = await catalog.SaveAsync(null, "Game", playnite.ToString(), null, null, true, Guid.NewGuid(), default);
        await catalog.ReportInventoryAsync(station.Id, [new(game.Id, playnite, true)], default);
        Assert.True((await db.Set<StationGame>().AsNoTracking().SingleAsync()).Installed);
        await catalog.ReportInventoryAsync(station.Id, [], default);
        Assert.False((await db.Set<StationGame>().AsNoTracking().SingleAsync()).Installed);
        await catalog.ReportInventoryAsync(station.Id, [new(game.Id, playnite, true)], default);
        await catalog.SaveAsync(game.Id, "Game", Guid.NewGuid().ToString(), null, null, true, Guid.NewGuid(), default);
        Assert.False((await db.Set<StationGame>().AsNoTracking().SingleAsync()).Installed);
    }

    [PostgresFact]
    public async Task Inventory_RejectsUnknownMismatchedDuplicateAndOversizedEntries()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var station = new Station(Guid.NewGuid(), "PC", "PC", null, "test", DateTime.UtcNow);
        db.Stations.Add(station);
        await db.SaveChangesAsync();
        var catalog = new GameCatalogService(new ClubData(db), TimeProvider.System);
        var playnite = Guid.NewGuid();
        var game = await catalog.SaveAsync(null, "Game", playnite.ToString(), null, null, true, Guid.NewGuid(), default);
        foreach (var entries in new IReadOnlyList<StationGameInventoryEntry>[] {
            [new(Guid.NewGuid(), playnite, true)], [new(game.Id, Guid.NewGuid(), true)],
            [new(game.Id, playnite, true), new(game.Id, playnite, true)],
            Enumerable.Range(0, 501).Select(_ => new StationGameInventoryEntry(Guid.NewGuid(), Guid.NewGuid(), true)).ToArray() })
            await Assert.ThrowsAsync<ClubException>(() => catalog.ReportInventoryAsync(station.Id, entries, default));
        Assert.Empty(await db.Set<StationGame>().ToListAsync());
    }
}
