using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Application.Gaming;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Billing;

[Collection(PostgresCollection.Name)]
public sealed class SessionBillingTests(PostgresFixture postgres)
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void PostpaidFunding_SetsServerDeadlineDespiteNullPurchasedMinutes()
    {
        var session = Postpaid(10m, 360);
        session.Start(Now);
        Assert.Null(session.PurchasedMinutes);
        Assert.Equal(Now.AddMinutes(6), session.ExpectedEndAtUtc);
        Assert.Equal(360, session.RemainingSeconds(Now));
        Assert.Equal(0, session.RemainingSeconds(Now.AddHours(1)));
        session.End(Now.AddHours(1));
        Assert.Equal(360, session.AccumulatedSeconds);
        Assert.Equal(1m, Money.Charge(session.HourlyPriceSnapshot!.Value, session.AccumulatedTicks));
    }

    [Fact]
    public void PostpaidPause_DoesNotConsumeTimeOrFunding()
    {
        var session = Postpaid(60m, 60);
        session.Start(Now);
        session.Pause(Now.AddSeconds(15));
        Assert.Equal(45, session.RemainingSeconds(Now.AddHours(1)));
        session.Resume(Now.AddHours(1));
        Assert.Equal(Now.AddHours(1).AddSeconds(45), session.ExpectedEndAtUtc);
        session.End(Now.AddHours(1).AddSeconds(15));
        Assert.Equal(0.50m, Money.Charge(60m, session.AccumulatedTicks));
    }

    [Fact]
    public void PostpaidSubsecondUsage_IsNotRoundedToWholeSecondsBeforeBilling()
    {
        var session = Postpaid(36m, 60);
        session.Start(Now);
        session.End(Now.AddMilliseconds(500));
        Assert.Equal(0, session.AccumulatedSeconds);
        Assert.Equal(0.01m, Money.Charge(36m, session.AccumulatedTicks));
    }

    [Fact]
    public void BillingConfiguration_CannotBeChangedAfterSnapshot()
    {
        var session = Postpaid(10m, 360);
        Assert.Throws<InvalidOperationException>(() =>
            session.ConfigureBilling(BillingMode.Postpaid, 100m, null, 36, Fingerprint));
        session.Start(Now);
        Assert.Throws<InvalidOperationException>(() =>
            session.ConfigureBilling(BillingMode.Postpaid, 100m, null, 36, Fingerprint));
        Assert.Equal(10m, session.HourlyPriceSnapshot);
    }

    [Fact]
    public void PostpaidConfiguration_RejectsUnlimitedUnfundedOrOverlongSession()
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now);
        session.SetPricing(Guid.NewGuid(), null, 0m);
        Assert.Throws<ArgumentException>(() => session.ConfigureBilling(BillingMode.Postpaid, 10m, null, null, Fingerprint));
        Assert.Throws<ArgumentException>(() => session.ConfigureBilling(BillingMode.Postpaid, 10m, null, 0, Fingerprint));
        Assert.Throws<ArgumentException>(() => session.ConfigureBilling(BillingMode.Postpaid, 10m, null, 604801, Fingerprint));
    }

    [Fact]
    public void PurchaseFingerprint_NormalizesEquivalentDecimalScaleButDetectsParameterChanges()
    {
        var purchase = new SessionPurchase(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BillingMode.Postpaid,
            Guid.NewGuid(), null, null, 1m);
        Assert.Equal(SessionBillingService.Fingerprint(purchase), SessionBillingService.Fingerprint(purchase with { PostpaidLimit = 1.00m }));
        Assert.NotEqual(SessionBillingService.Fingerprint(purchase), SessionBillingService.Fingerprint(purchase with { PostpaidLimit = 2m }));
        Assert.NotEqual(SessionBillingService.Fingerprint(purchase), SessionBillingService.Fingerprint(purchase with { StationId = Guid.NewGuid() }));
    }

    [Fact]
    public void PurchaseFingerprint_RejectsMalformedIdentityAndMoney()
    {
        var purchase = new SessionPurchase(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BillingMode.Postpaid,
            Guid.NewGuid(), null, null, 1m);
        Assert.Equal("INVALID_PURCHASE", Assert.Throws<ClubException>(() =>
            SessionBillingService.Fingerprint(purchase with { OperationId = Guid.Empty })).Code);
        Assert.Equal("INVALID_AMOUNT", Assert.Throws<ClubException>(() =>
            SessionBillingService.Fingerprint(purchase with { PostpaidLimit = 1.001m })).Code);
    }

    [Fact]
    public void PackageHardWindow_ExpiresEvenWhenPaidClockIsPaused()
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 60, Now);
        session.SetWindowEnd(Now.AddMinutes(60));
        session.Start(Now);
        session.Pause(Now.AddMinutes(10));
        Assert.Equal(600, session.ElapsedSeconds(Now.AddHours(2)));
        Assert.Equal(0, session.RemainingSeconds(Now.AddHours(1)));
        Assert.True(session.IsExpiredAt(Now.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => session.Resume(Now.AddHours(1)));
        session.End(Now.AddHours(2));
        Assert.Equal(Now.AddHours(1), session.EndedAtUtc);
        Assert.Equal(600, session.AccumulatedSeconds);
    }

    [Fact]
    public void PackageResume_CannotPushDeadlinePastWindow()
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 60, Now);
        session.SetWindowEnd(Now.AddMinutes(60));
        session.Start(Now);
        session.Pause(Now.AddMinutes(10));
        session.Resume(Now.AddMinutes(30));
        Assert.Equal(Now.AddMinutes(60), session.ExpectedEndAtUtc);
        session.End(Now.AddHours(2));
        Assert.Equal(2400, session.AccumulatedSeconds);
    }

    [Fact]
    public void PackageWindow_RejectsMissingDstHourAndChoosesEarliestRepeatedHour()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("BillingTestDst", TimeSpan.FromHours(1), "BillingTestDst",
            "Standard", "Summer", [rule]);
        Assert.Equal("PACKAGE_WINDOW_INVALID", Assert.Throws<ClubException>(() =>
            SessionBillingService.ResolveWindowEndUtc(new DateTime(2026, 3, 29, 2, 30, 0), zone)).Code);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc),
            SessionBillingService.ResolveWindowEndUtc(new DateTime(2026, 10, 25, 2, 30, 0), zone));
    }

    [PostgresFact]
    public async Task AuthLifetime_RejectsUnusablePrepaidTimeAndCapsPostpaidFunding()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 1000m);
        await using var context = database.CreateContext();
        var service = CreateService(context, clock);
        var error = await Assert.ThrowsAsync<ClubException>(() => service.StartPaidAsync(
            Purchase(seed, BillingMode.Prepaid, minutes: 721), null, default));
        Assert.Equal("SESSION_EXCEEDS_AUTH_LIFETIME", error.Code);
        var postpaid = await service.StartPaidAsync(Purchase(seed, BillingMode.Postpaid), null, default);
        Assert.Equal(12 * 60 * 60, postpaid.RemainingSeconds);
        Assert.Equal(1000m, (await context.Set<Wallet>().SingleAsync()).Balance);
        Assert.False(await context.Set<WalletTransaction>().AnyAsync(t => t.Type == WalletTransactionType.GamingCharge));
    }

    [PostgresFact]
    public async Task Prepaid_StartDebitsRoundedPriceOnceAndEarlyEndDoesNotRefund()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 0.30m, 1m);
        await using var context = database.CreateContext();
        var service = CreateService(context, clock);
        var purchase = Purchase(seed, BillingMode.Prepaid, minutes: 1);
        var first = await service.StartPaidAsync(purchase, null, default);
        var repeated = await service.StartPaidAsync(purchase, null, default);
        Assert.Equal(first.Id, repeated.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.EndAsync(first.Id, null, default);
        await service.EndAsync(first.Id, null, default);

        await using var verify = database.CreateContext();
        var session = await verify.Set<GamingSession>().SingleAsync();
        Assert.Equal(0.01m, session.InitialPrice);
        Assert.Equal(0.01m, session.PrepaidCharged);
        Assert.Equal(0.01m, session.FinalPrice);
        Assert.Equal(0.99m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(1, await verify.Set<WalletTransaction>().CountAsync(t => t.Type == WalletTransactionType.GamingCharge));
        Assert.Equal(2, await verify.Set<SessionEvent>().CountAsync());
    }

    [PostgresFact]
    public async Task Postpaid_ReservesThenChargesActualUsageAndReleasesRemainderOnLogout()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 10m);
        await using var context = database.CreateContext();
        var service = CreateService(context, clock);
        var started = await service.StartPaidAsync(Purchase(seed, BillingMode.Postpaid, limit: 5m), null, default);
        Assert.Equal(300, started.RemainingSeconds);
        Assert.Equal(10m, (await context.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(5m, (await context.Set<WalletReservation>().SingleAsync()).Amount);
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.PauseAsync(started.Id, null, default);
        clock.Advance(TimeSpan.FromMinutes(20));
        await service.ResumeAsync(started.Id, null, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.LogoutStationAsync(seed.StationId, null, default);
        await service.LogoutStationAsync(seed.StationId, null, default);

        await using var verify = database.CreateContext();
        var session = await verify.Set<GamingSession>().SingleAsync();
        Assert.Equal(60, session.AccumulatedSeconds);
        Assert.Equal(1m, session.FinalPrice);
        Assert.Equal(9m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.NotNull((await verify.Set<WalletReservation>().SingleAsync()).ReleasedAtUtc);
        Assert.Equal(1, await verify.Set<WalletTransaction>().CountAsync(t => t.Type == WalletTransactionType.GamingCharge));
    }

    [PostgresFact]
    public async Task Postpaid_FundingDeadlineCapsDelayedMonitorChargeAndIgnoresLaterTariffChange()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 1m);
        await using var context = database.CreateContext();
        var service = CreateService(context, clock);
        await service.StartPaidAsync(Purchase(seed, BillingMode.Postpaid), null, default);
        (await context.Set<Tariff>().SingleAsync()).UpdatePrice(600m);
        await context.SaveChangesAsync();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await service.CompleteDueAsync(default));
        await using var verify = database.CreateContext();
        var session = await verify.Set<GamingSession>().SingleAsync();
        Assert.Equal(60m, session.HourlyPriceSnapshot);
        Assert.Equal(1m, session.FinalPrice);
        Assert.Equal(60, session.AccumulatedSeconds);
        Assert.Equal(0m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task InsufficientPrepaidFunds_RollBackCreatedSessionAndLedger()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 1m);
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<ClubException>(() => CreateService(context, clock)
            .StartPaidAsync(Purchase(seed, BillingMode.Prepaid, minutes: 60), null, default));
        Assert.Equal("INSUFFICIENT_FUNDS", error.Code);
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Set<GamingSession>().ToListAsync());
        Assert.Empty(await verify.Set<SessionEvent>().ToListAsync());
        Assert.Equal(1m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.False(await verify.Set<WalletTransaction>().AnyAsync(t => t.Type == WalletTransactionType.GamingCharge));
    }

    [PostgresFact]
    public async Task InsufficientPostpaidReserve_RollsBackCreatedSessionAndHold()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 1m);
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<ClubException>(() => CreateService(context, clock)
            .StartPaidAsync(Purchase(seed, BillingMode.Postpaid, limit: 2m), null, default));
        Assert.Equal("INSUFFICIENT_FUNDS", error.Code);
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Set<GamingSession>().ToListAsync());
        Assert.Empty(await verify.Set<WalletReservation>().ToListAsync());
        Assert.Equal(1m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task ExistingOperationWithChangedPurchase_IsRejectedWithoutAdditionalDebit()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 100m);
        await using var context = database.CreateContext();
        var service = CreateService(context, clock);
        var purchase = Purchase(seed, BillingMode.Prepaid, minutes: 10);
        await service.StartPaidAsync(purchase, null, default);
        var error = await Assert.ThrowsAsync<ClubException>(() =>
            service.StartPaidAsync(purchase with { PurchasedMinutes = 20 }, null, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        await using var verify = database.CreateContext();
        Assert.Equal(90m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Single(await verify.Set<GamingSession>().ToListAsync());
    }

    [PostgresFact]
    public async Task ClosedPackageAndWrongGroup_AreRejectedBeforeAnyCharge()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 60m, 100m);
        await using var context = database.CreateContext();
        var groupId = (await context.Stations.SingleAsync()).StationGroupId!.Value;
        var package = new TariffPackage(Guid.NewGuid(), "Night", groupId, 600, 20m, new TimeOnly(22, 0), new TimeOnly(8, 0));
        var wrongGroup = new Tariff(Guid.NewGuid(), "Wrong group", Guid.Parse("10000000-0000-0000-0000-000000000002"), 10m);
        context.Set<TariffPackage>().Add(package);
        context.Set<Tariff>().Add(wrongGroup);
        await context.SaveChangesAsync();
        var service = CreateService(context, clock);
        var closed = Purchase(seed, BillingMode.Prepaid) with { TariffId = null, PackageId = package.Id };
        Assert.Equal("PACKAGE_UNAVAILABLE", (await Assert.ThrowsAsync<ClubException>(() => service.StartPaidAsync(closed, null, default))).Code);
        var wrong = Purchase(seed, BillingMode.Prepaid, minutes: 10) with { TariffId = wrongGroup.Id };
        Assert.Equal("STATION_GROUP_MISMATCH", (await Assert.ThrowsAsync<ClubException>(() => service.StartPaidAsync(wrong, null, default))).Code);
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Set<GamingSession>().ToListAsync());
        Assert.Equal(100m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task DepositOperationId_CannotBeReusedForPostpaidPurchase()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 6m, 10m);
        await using var context = database.CreateContext();
        var depositId = await context.Set<WalletTransaction>().Select(t => t.OperationId).SingleAsync();
        var purchase = Purchase(seed, BillingMode.Postpaid, limit: 5m) with { OperationId = depositId };
        var error = await Assert.ThrowsAsync<ClubException>(() => CreateService(context, clock).StartPaidAsync(purchase, null, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Set<GamingSession>().ToListAsync());
        Assert.Empty(await verify.Set<WalletReservation>().ToListAsync());
        Assert.Empty(await verify.Set<SessionEvent>().ToListAsync());
        Assert.Equal(10m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task PostpaidOperationId_CannotBeReusedForDeposit_AndSessionStillSettles()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 6m, 10m);
        await using var context = database.CreateContext();
        var service = CreateService(context, clock);
        var started = await service.StartPaidAsync(Purchase(seed, BillingMode.Postpaid, limit: 5m), null, default);
        var wallet = new WalletService(new ClubData(context), clock, new WalletPolicy());
        var error = await Assert.ThrowsAsync<ClubException>(() => wallet.DepositAsync(seed.UserId, 1m, started.Id, null, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        clock.Advance(TimeSpan.FromSeconds(3));
        await service.EndAsync(started.Id, null, default);
        await using var verify = database.CreateContext();
        var game = await verify.Set<GamingSession>().SingleAsync();
        Assert.Equal(GamingSessionStatus.Completed, game.Status);
        Assert.Equal(0.01m, game.FinalPrice);
        Assert.Equal(9.99m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.NotNull((await verify.Set<WalletReservation>().SingleAsync()).ReleasedAtUtc);
        var transaction = await verify.Set<WalletTransaction>().SingleAsync(t => t.OperationId == started.Id);
        Assert.Equal(WalletTransactionType.GamingCharge, transaction.Type);
        Assert.Equal(-0.01m, transaction.Amount);
    }

    [PostgresFact]
    public async Task ConcurrentDepositAndPostpaidWithSameOperationId_HaveOnlyOneOwnerAndNoStuckSession()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock, 6m, 10m);
        var purchase = Purchase(seed, BillingMode.Postpaid, limit: 5m);
        await using var gamingContext = database.CreateContext();
        await using var walletContext = database.CreateContext();
        var service = CreateService(gamingContext, clock);
        var wallet = new WalletService(new ClubData(walletContext), clock, new WalletPolicy());
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => service.StartPaidAsync(purchase, null, default)),
            Record.ExceptionAsync(() => wallet.DepositAsync(seed.UserId, 1m, purchase.OperationId, null, default)));
        Assert.Single(outcomes, exception => exception is null);
        Assert.Equal("IDEMPOTENCY_CONFLICT", Assert.IsType<ClubException>(Assert.Single(outcomes, exception => exception is not null)).Code);

        if (outcomes[0] is null)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            await service.EndAsync(purchase.OperationId, null, default);
        }
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<GamingSession>().AnyAsync(s => s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused));
        Assert.False(await verify.Set<WalletReservation>().AnyAsync(r => r.ReleasedAtUtc == null));
        var owner = await verify.Set<WalletTransaction>().SingleAsync(t => t.OperationId == purchase.OperationId);
        Assert.Equal(outcomes[0] is null ? WalletTransactionType.GamingCharge : WalletTransactionType.Deposit, owner.Type);
        Assert.Equal(outcomes[0] is null ? 9.99m : 11m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    private static GamingSession Postpaid(decimal hourly, int fundedSeconds)
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now);
        session.SetPricing(Guid.NewGuid(), null, 0m);
        session.ConfigureBilling(BillingMode.Postpaid, hourly, null, fundedSeconds, Fingerprint);
        return session;
    }

    private static GamingSessionService CreateService(GameClubDbContext context, MutableClock clock)
    {
        var data = new ClubData(context);
        return new GamingSessionService(data, clock, new NoEvents(),
            new SessionBillingService(data, new WalletService(data, clock, new WalletPolicy()), new SessionBillingOptions()));
    }

    private static SessionPurchase Purchase(Seed seed, BillingMode mode, int? minutes = null, decimal? limit = null) =>
        new(Guid.NewGuid(), seed.UserId, seed.StationId, mode, seed.TariffId, null, minutes, limit);

    private static async Task<Seed> SeedAsync(PostgresTestDatabase database, MutableClock clock, decimal hourlyPrice, decimal deposit)
    {
        await using var context = database.CreateContext();
        var station = new Station(Guid.NewGuid(), "Billing PC", "BILLING-" + Guid.NewGuid().ToString("N"), null, "0.1.0", Now);
        var user = new User(Guid.NewGuid(), "bill_" + Guid.NewGuid().ToString("N")[..12], "Billing test", null, null, Now);
        user.SetPasswordHash("Integration fixture only; not an authentication test");
        var tariff = new Tariff(Guid.NewGuid(), "Hourly", station.StationGroupId!.Value, hourlyPrice);
        context.Stations.Add(station);
        context.Users.Add(user);
        context.Set<Tariff>().Add(tariff);
        context.PlayerAuthSessions.Add(new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, Now, Now.AddHours(12)));
        await context.SaveChangesAsync();
        var data = new ClubData(context);
        await new WalletService(data, clock, new WalletPolicy()).DepositAsync(user.Id, deposit, Guid.NewGuid(), null, default);
        return new Seed(user.Id, station.Id, tariff.Id);
    }

    private sealed record Seed(Guid UserId, Guid StationId, Guid TariffId);
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(Now);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class NoEvents : IClubEvents
    {
        public Task StationChangedAsync(Guid stationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
