using System.Globalization;
using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Domain.Billing;
using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Billing;

public sealed class CatalogTests
{
    [Theory]
    [InlineData("  Standard ", "Standard", "STANDARD")]
    [InlineData("VIP", "VIP", "VIP")]
    [InlineData("Bootcamp", "Bootcamp", "BOOTCAMP")]
    [InlineData("Custom room", "Custom room", "CUSTOM ROOM")]
    public void StationGroup_TrimsAndNormalizesName(string value, string name, string normalized)
    {
        var group = new StationGroup(Guid.NewGuid(), value);
        Assert.Equal(name, group.Name);
        Assert.Equal(normalized, group.NormalizedName);
    }

    [Fact]
    public void InvalidIdsAndNames_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new StationGroup(Guid.Empty, "Standard"));
        Assert.Throws<ArgumentException>(() => new StationGroup(Guid.NewGuid(), " "));
        Assert.Throws<ArgumentException>(() => new StationGroup(Guid.NewGuid(), new string('a', 101)));
        Assert.Throws<ArgumentException>(() => new Tariff(Guid.NewGuid(), "Basic", Guid.Empty, 1m));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.001")]
    [InlineData("1.005")]
    [InlineData("10000000000000000")]
    public void InvalidPrices_AreRejectedInsteadOfSilentlyRounded(string rawPrice)
    {
        var price = decimal.Parse(rawPrice, CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Tariff(Guid.NewGuid(), "Basic", Guid.NewGuid(), price));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TariffPackage(Guid.NewGuid(), "Hour", Guid.NewGuid(), 60, price));
    }

    [Fact]
    public void PriceUpdate_ValidatesBeforeMutatingAndAllowsMinorCurrencyUnit()
    {
        var tariff = new Tariff(Guid.NewGuid(), "Basic", Guid.NewGuid(), 1m);
        Assert.Throws<ArgumentOutOfRangeException>(() => tariff.UpdatePrice(1.005m));
        Assert.Equal(1m, tariff.HourlyPrice);
        tariff.UpdatePrice(0.01m);
        Assert.Equal(0.01m, tariff.HourlyPrice);
        tariff.UpdatePrice(Tariff.MaximumPrice);
        Assert.Equal(Tariff.MaximumPrice, tariff.HourlyPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10081)]
    public void InvalidPackageDuration_IsRejected(int duration) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TariffPackage(Guid.NewGuid(), "Package", Guid.NewGuid(), duration, 10m));

    [Fact]
    public void AvailabilityWindow_RequiresTwoDistinctEndpoints()
    {
        Assert.Throws<ArgumentException>(() => new TariffPackage(Guid.NewGuid(), "Night", Guid.NewGuid(),
            600, 20m, new TimeOnly(22, 0)));
        Assert.Throws<ArgumentException>(() => new TariffPackage(Guid.NewGuid(), "Night", Guid.NewGuid(),
            600, 20m, availableUntil: new TimeOnly(8, 0)));
        Assert.Throws<ArgumentException>(() => new TariffPackage(Guid.NewGuid(), "Night", Guid.NewGuid(),
            600, 20m, new TimeOnly(8, 0), new TimeOnly(8, 0)));
    }

    [Theory]
    [InlineData(22, 0, 600)]
    [InlineData(23, 0, 540)]
    [InlineData(0, 0, 480)]
    [InlineData(7, 0, 60)]
    [InlineData(7, 59, 1)]
    public void NightPackage_CapsPurchasedMinutesAtNextWindowEnd(int hour, int minute, int expected)
    {
        var package = NightPackage();
        Assert.Equal(expected, package.GetPurchasedMinutes(LocalTime(hour, minute)));
        Assert.Equal(20m, package.Price);
        Assert.Equal(600, package.DurationMinutes);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(12, 0)]
    [InlineData(21, 59)]
    public void NightPackage_RejectsOutsideWindowIncludingExclusiveEnd(int hour, int minute) =>
        Assert.Throws<InvalidOperationException>(() => NightPackage().GetPurchasedMinutes(LocalTime(hour, minute)));

    [Fact]
    public void WindowWithLessThanOneWholeMinuteRemaining_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() =>
            NightPackage().GetPurchasedMinutes(LocalTime(7, 59).AddSeconds(1)));

    [Theory]
    [InlineData(22, 0, 6)]
    [InlineData(23, 59, 6)]
    [InlineData(0, 0, 5)]
    [InlineData(7, 59, 5)]
    public void NightWindowEnd_UsesCorrectLocalCalendarDay(int hour, int minute, int expectedDay)
    {
        Assert.Equal(new DateTime(2026, 9, expectedDay, 8, 0, 0, DateTimeKind.Unspecified),
            NightPackage().GetWindowEndLocal(LocalTime(hour, minute)));
    }

    [Fact]
    public void WindowEnd_HasNoDeadlineWithoutWindowAndRejectsUnavailableStart()
    {
        var anytime = new TariffPackage(Guid.NewGuid(), "Anytime", Guid.NewGuid(), 60, 10m);
        Assert.Null(anytime.GetWindowEndLocal(LocalTime(12, 0)));
        Assert.Throws<InvalidOperationException>(() => NightPackage().GetWindowEndLocal(LocalTime(8, 0)));
        Assert.Equal(DateTimeKind.Unspecified,
            NightPackage().GetWindowEndLocal(DateTime.SpecifyKind(LocalTime(22, 0), DateTimeKind.Utc))!.Value.Kind);
    }

    [Fact]
    public void DaytimePackage_RespectsFixedDurationAndWindowCap()
    {
        var package = new TariffPackage(Guid.NewGuid(), "Daytime", Guid.NewGuid(), 120, 12m,
            new TimeOnly(10, 0), new TimeOnly(18, 0));
        Assert.Equal(120, package.GetPurchasedMinutes(LocalTime(10, 0)));
        Assert.Equal(30, package.GetPurchasedMinutes(LocalTime(17, 30)));
        Assert.Throws<InvalidOperationException>(() => package.GetPurchasedMinutes(LocalTime(18, 0)));
        Assert.Throws<InvalidOperationException>(() => package.GetPurchasedMinutes(LocalTime(9, 59)));
    }

    [Fact]
    public void PackageWithoutWindow_HasFixedDurationAndCanBeDeactivated()
    {
        var package = new TariffPackage(Guid.NewGuid(), "Week", Guid.NewGuid(), 10080, 100m);
        Assert.Equal(10080, package.GetPurchasedMinutes(LocalTime(3, 30)));
        package.SetActive(false);
        Assert.Throws<InvalidOperationException>(() => package.GetPurchasedMinutes(LocalTime(3, 30)));
        package.SetActive(true);
        Assert.Equal(10080, package.GetPurchasedMinutes(LocalTime(3, 30)));
    }

    [Fact]
    public async Task CreateAndUpdateCatalog_AuditChangesInsideAtomicOperations()
    {
        await using var db = CreateContext();
        var data = new CatalogData(db);
        var service = new CatalogService(data, TimeProvider.System);
        var actor = Guid.NewGuid();
        var group = await service.CreateGroupAsync("Standard", actor, default);
        var tariff = await service.CreateTariffAsync("Standard hourly", group.Id, 900m, actor, default);
        var package = await service.CreatePackageAsync("Night", group.Id, 600, 2500m,
            new TimeOnly(22, 0), new TimeOnly(8, 0), actor, default);
        await service.UpdateTariffPriceAsync(tariff.Id, 1200m, actor, default);
        await service.SetTariffActiveAsync(tariff.Id, false, actor, default);
        await service.SetPackageActiveAsync(package.Id, false, actor, default);

        Assert.Equal(6, data.AtomicCalls);
        Assert.Equal(6, await db.Set<SecurityAuditEvent>().CountAsync());
        Assert.Equal(1200m, tariff.HourlyPrice);
        Assert.False(tariff.IsActive);
        Assert.False(package.IsActive);
        var priceAudit = await db.Set<SecurityAuditEvent>().SingleAsync(e => e.EventType == "TariffPriceChanged");
        Assert.Contains("OldPrice=900.00;NewPrice=1200.00", priceAudit.Details);
        Assert.Contains($"EmployeeId={actor:D}", priceAudit.Details);
    }

    [Fact]
    public async Task DuplicateGroup_IsRejectedCaseInsensitively()
    {
        await using var db = CreateContext();
        var service = new CatalogService(new CatalogData(db), TimeProvider.System);
        await service.CreateGroupAsync("Standard", null, default);

        var error = await Assert.ThrowsAsync<ClubException>(() =>
            service.CreateGroupAsync(" STANDARD ", null, default));

        Assert.Equal("GROUP_ALREADY_EXISTS", error.Code);
        Assert.Equal(1, await db.Set<StationGroup>().CountAsync());
        Assert.Equal(1, await db.Set<SecurityAuditEvent>().CountAsync());
    }

    [Fact]
    public async Task MissingGroup_IsRejectedWithoutCreatingTariff()
    {
        await using var db = CreateContext();
        var service = new CatalogService(new CatalogData(db), TimeProvider.System);

        var error = await Assert.ThrowsAsync<ClubException>(() =>
            service.CreateTariffAsync("Basic", Guid.NewGuid(), 100m, null, default));

        Assert.Equal("GROUP_NOT_FOUND", error.Code);
        Assert.Empty(db.Set<Tariff>());
        Assert.Empty(db.Set<SecurityAuditEvent>());
    }

    private static TariffPackage NightPackage() => new(Guid.NewGuid(), "Night", Guid.NewGuid(),
        600, 20m, new TimeOnly(22, 0), new TimeOnly(8, 0));

    private static DateTime LocalTime(int hour, int minute) =>
        new(2026, 9, 5, hour, minute, 0, DateTimeKind.Unspecified);

    private static CatalogContext CreateContext() => new(new DbContextOptionsBuilder<CatalogContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class CatalogContext(DbContextOptions<CatalogContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StationGroup>();
            modelBuilder.Entity<Tariff>();
            modelBuilder.Entity<TariffPackage>();
            modelBuilder.Entity<SecurityAuditEvent>();
        }
    }

    private sealed class CatalogData(CatalogContext db) : IClubData
    {
        public int AtomicCalls { get; private set; }
        public IQueryable<T> Query<T>() where T : class => db.Set<T>();
        public void Add<T>(T entity) where T : class => db.Add(entity);
        public async Task SaveAsync(CancellationToken cancellationToken) => await db.SaveChangesAsync(cancellationToken);
        public async Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            AtomicCalls++;
            var result = await operation(cancellationToken);
            await SaveAsync(cancellationToken);
            return result;
        }
    }
}
