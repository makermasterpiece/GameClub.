using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class Stage8MigrationUpgradeTests(PostgresFixture postgres)
{
    private const string Stage7 = "20260905130313_AddEmployeeAdministrationAndClientHealth";
    private const string Stage8 = "20260906105627_AddSessionExtensionsTransfersAndStationHistory";
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task PopulatedStage7Upgrade_BackfillsOriginalResidencyAndPackageSnapshotsWithoutInventingHistory()
    {
        await using var database = await postgres.CreateDatabaseAsync(Stage7);
        await using var context = database.CreateContext();
        Assert.DoesNotContain(Stage8, await context.Database.GetAppliedMigrationsAsync());
        var groupId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var package = new TariffPackage(Guid.NewGuid(), "Original package", groupId, 120, 9m);
        context.Add(package);
        var rows = new[]
        {
            NewRow("Active", Now, null, null, null),
            NewRow("Paused", Now, null, package.Id, 5m),
            NewRow("Completed", Now, Now.AddMinutes(40), package.Id, 7m),
            NewRow("Completed", Now, null, package.Id, null),
            NewRow("Created", null, null, null, null),
            NewRow("Cancelled", null, Now.AddMinutes(1), null, null)
        };
        foreach (var row in rows)
        {
            var station = new Station(row.StationId, "Upgrade PC", "UPGRADE-" + row.StationId.ToString("N"), null, "0.1.0", Now);
            var user = new User(row.UserId, "upgrade_" + row.UserId.ToString("N")[..12], "Migration fixture", null, null, Now);
            user.SetPasswordHash("Migration fixture only");
            context.AddRange(station, user);
        }
        await context.SaveChangesAsync();

        // Raw SQL deliberately writes the Stage7 shape: the latest EF entity would reference columns
        // that do not exist yet, and a fresh latest-schema seed cannot exercise these backfills.
        foreach (var row in rows)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO gaming_sessions
                    ("Id", "UserId", "StationId", "PackageId", "Status", "CreatedAtUtc", "StartedAtUtc", "EndedAtUtc",
                     "PurchasedMinutes", "AddedMinutes", "InitialPrice", "PrepaidCharged", "AccumulatedTicks", "LastStateChangedAtUtc")
                VALUES ({row.Id}, {row.UserId}, {row.StationId}, {row.PackageId}, {row.Status}, {Now}, {row.StartedAtUtc},
                    {row.EndedAtUtc}, 60, 0, {row.InitialPrice}, {row.InitialPrice}, {TimeSpan.FromMinutes(20).Ticks}, {Now.AddHours(1)})
                """);
        }

        await context.Database.MigrateAsync();
        Assert.Contains(Stage8, await context.Database.GetAppliedMigrationsAsync());
        context.ChangeTracker.Clear();
        var segments = await context.Set<StationSessionSegment>().AsNoTracking().ToListAsync();
        Assert.Equal(4, segments.Count);
        foreach (var row in rows)
        {
            if (row.StartedAtUtc is null)
            {
                Assert.DoesNotContain(segments, segment => segment.GamingSessionId == row.Id);
                continue;
            }
            var segment = Assert.Single(segments, s => s.GamingSessionId == row.Id);
            Assert.Equal(row.StationId, segment.StationId);
            Assert.Equal(row.StartedAtUtc.Value, segment.StartedAtUtc);
            var expectedEnd = row.Status is "Active" or "Paused" ? (DateTime?)null : row.EndedAtUtc ?? Now.AddHours(1);
            Assert.Equal(expectedEnd, segment.EndedAtUtc);
        }

        var sessions = await context.Set<GamingSession>().AsNoTracking().ToListAsync();
        Assert.Equal(rows.Length, sessions.Count);
        foreach (var row in rows)
        {
            var session = Assert.Single(sessions, s => s.Id == row.Id);
            Assert.Equal(row.Status, session.Status.ToString());
            Assert.Equal(row.StationId, session.StationId);
            Assert.Equal(row.InitialPrice, session.InitialPrice);
            Assert.Equal(TimeSpan.FromMinutes(20).Ticks, session.AccumulatedTicks);
            Assert.Equal(row.PackageId is null ? (decimal?)null : row.InitialPrice ?? package.Price, session.PackagePriceSnapshot);
            Assert.Equal(row.PackageId is null ? (int?)null : package.DurationMinutes, session.PackageDurationMinutesSnapshot);
        }
        Assert.False(await context.Set<SessionOperation>().AnyAsync());
        Assert.False(await context.Set<SessionEvent>().AnyAsync());

        // Reapplying latest must neither duplicate segments nor replace captured prices.
        var segmentIds = segments.Select(s => s.Id).Order().ToArray();
        await context.Database.MigrateAsync();
        Assert.Equal(segmentIds, await context.Set<StationSessionSegment>().OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync());
    }

    private static LegacySession NewRow(string status, DateTime? started, DateTime? ended, Guid? packageId, decimal? initialPrice) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), status, started, ended, packageId, initialPrice);

    private sealed record LegacySession(Guid Id, Guid UserId, Guid StationId, string Status,
        DateTime? StartedAtUtc, DateTime? EndedAtUtc, Guid? PackageId, decimal? InitialPrice);
}
