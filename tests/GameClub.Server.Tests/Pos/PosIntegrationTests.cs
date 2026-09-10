using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Application.Pos;
using GameClub.Domain.Billing;
using GameClub.Domain.Employees;
using GameClub.Domain.Gaming;
using GameClub.Domain.Pos;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Pos;

[Collection(PostgresCollection.Name)]
public sealed class PosIntegrationTests(PostgresFixture postgres)
{
    private static readonly TimeProvider Clock = new FixedClock();

    [PostgresFact]
    public async Task PaymentMethods_UseSeparateLedgersAndImmutablePriceSnapshots()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        foreach (var method in Enum.GetValues<PaymentMethod>())
        {
            await using var context = db.CreateContext();
            var sale = await Sales(context).PurchaseAsync(Cart(seed, method), seed.Employee, default);
            Assert.Equal(10m, sale.CapturedTotal);
        }
        await using (var context = db.CreateContext())
        {
            var product = await context.Set<Product>().SingleAsync();
            product.Update(null, "Renamed", 99m, true);
            await context.SaveChangesAsync();
        }
        await using var verify = db.CreateContext();
        Assert.Equal(7, (await verify.Set<Product>().SingleAsync()).StockQuantity);
        Assert.Equal(90m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(2, await verify.Set<Payment>().CountAsync());
        Assert.Equal(1, await verify.Set<WalletTransaction>().CountAsync());
        Assert.All(await verify.Set<SaleItem>().ToListAsync(), x => { Assert.Equal("Drink", x.NameSnapshot); Assert.Equal(10m, x.UnitPrice); });
    }

    [PostgresFact]
    public async Task InsufficientStockOrWallet_RollsBackAllFinancialAndInventoryWrites()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db, stock: 1, balance: 5m);
        await using var context = db.CreateContext();
        await Assert.ThrowsAsync<ClubException>(() => Sales(context).PurchaseAsync(Cart(seed, PaymentMethod.Wallet), seed.Employee, default));
        await using var second = db.CreateContext();
        await Assert.ThrowsAsync<ClubException>(() => Sales(second).PurchaseAsync(Cart(seed, PaymentMethod.Cash) with { Items = [new(seed.Product, 2)] }, seed.Employee, default));
        await using var verify = db.CreateContext();
        Assert.Equal(1, (await verify.Set<Product>().SingleAsync()).StockQuantity);
        Assert.Equal(5m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Empty(await verify.Set<Sale>().ToListAsync());
        Assert.Empty(await verify.Set<Payment>().ToListAsync());
        Assert.Empty(await verify.Set<WalletTransaction>().ToListAsync());
    }

    [PostgresFact]
    public async Task ChangedExpectedTotal_RequiresNewConfirmationWithoutAnySale()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        await using var context = db.CreateContext();
        await Assert.ThrowsAsync<ClubException>(() => Sales(context).PurchaseAsync(
            Cart(seed, PaymentMethod.Cash) with { ExpectedTotal = 9m }, seed.Employee, default));
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Set<Sale>().ToListAsync());
        Assert.Equal(10, (await verify.Set<Product>().SingleAsync()).StockQuantity);
    }

    [PostgresFact]
    public async Task WalletPurchase_CannotSpendGamingReservation()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        await using (var context = db.CreateContext())
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            var station = new Station(Guid.NewGuid(), "POS", "POS-TEST", null, "0.1", now);
            var session = new GamingSession(Guid.NewGuid(), seed.User, station.Id, 30, now);
            session.Start(now);
            context.AddRange(station, session);
            var wallet = await context.Set<Wallet>().SingleAsync();
            context.Add(new WalletReservation(Guid.NewGuid(), wallet.Id, session.Id, 95m, now));
            await context.SaveChangesAsync();
        }
        await using var saleContext = db.CreateContext();
        var error = await Assert.ThrowsAsync<ClubException>(() => Sales(saleContext).PurchaseAsync(Cart(seed, PaymentMethod.Wallet), seed.Employee, default));
        Assert.Equal("INSUFFICIENT_FUNDS", error.Code);
        await using var verify = db.CreateContext();
        Assert.Equal(100m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(10, (await verify.Set<Product>().SingleAsync()).StockQuantity);
        Assert.Empty(await verify.Set<Sale>().ToListAsync());
        Assert.Null((await verify.Set<WalletReservation>().SingleAsync()).ReleasedAtUtc);
    }

    [PostgresFact]
    public async Task SaleOperationId_CannotBeReusedAsWalletDeposit()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        var cart = Cart(seed, PaymentMethod.Cash);
        await using (var context = db.CreateContext()) await Sales(context).PurchaseAsync(cart, seed.Employee, default);
        await using var depositContext = db.CreateContext();
        var error = await Assert.ThrowsAsync<ClubException>(() => new WalletService(new ClubData(depositContext), Clock, new WalletPolicy())
            .DepositAsync(seed.User, 10m, cart.OperationId, seed.Employee, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        await using var verify = db.CreateContext();
        Assert.Equal(100m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Empty(await verify.Set<WalletTransaction>().ToListAsync());
    }

    [PostgresFact]
    public async Task InactiveCategory_BlocksPurchaseEvenWhenProductIsActive()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        await using (var context = db.CreateContext())
        {
            var category = new ProductCategory(Guid.NewGuid(), "Disabled", false);
            context.Add(category);
            (await context.Set<Product>().SingleAsync()).Update(category.Id, "Drink", 10m, true);
            await context.SaveChangesAsync();
        }
        await using var saleContext = db.CreateContext();
        await Assert.ThrowsAsync<ClubException>(() => Sales(saleContext).PurchaseAsync(Cart(seed, PaymentMethod.Cash), seed.Employee, default));
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Set<Sale>().ToListAsync());
    }

    [PostgresFact]
    public async Task DuplicatePurchase_IsIdempotentAndMismatchedCartRejected()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        var cart = Cart(seed, PaymentMethod.Wallet);
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var gate = new SaveGate();
        var result = await Task.WhenAll(Sales(first, gate).PurchaseAsync(cart, seed.Employee, default), Sales(second, gate).PurchaseAsync(cart, seed.Employee, default));
        Assert.Equal(result[0].Id, result[1].Id);
        await using var third = db.CreateContext();
        var error = await Assert.ThrowsAsync<ClubException>(() => Sales(third).PurchaseAsync(cart with { PaymentMethod = PaymentMethod.Cash }, seed.Employee, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        await using var verify = db.CreateContext();
        Assert.Single(await verify.Set<Sale>().ToListAsync());
        Assert.Single(await verify.Set<WalletTransaction>().ToListAsync());
        Assert.Equal(9, (await verify.Set<Product>().SingleAsync()).StockQuantity);
    }

    [PostgresFact]
    public async Task ConcurrentLastUnit_OnlyOneSaleCommits()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db, stock: 1);
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var gate = new SaveGate();
        var outcomes = await Task.WhenAll(Attempt(Sales(first, gate), Cart(seed, PaymentMethod.Cash), seed.Employee), Attempt(Sales(second, gate), Cart(seed, PaymentMethod.Cash), seed.Employee));
        Assert.Single(outcomes, x => x);
        await using var verify = db.CreateContext();
        Assert.Equal(0, (await verify.Set<Product>().SingleAsync()).StockQuantity);
        Assert.Single(await verify.Set<Sale>().ToListAsync());
        Assert.Single(await verify.Set<Payment>().ToListAsync());
    }

    [PostgresFact]
    public async Task FullRefund_EachMethodRestoresStockAndOriginalLedgerExactlyOnce()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        foreach (var method in Enum.GetValues<PaymentMethod>())
        {
            Guid saleId;
            await using (var context = db.CreateContext()) saleId = (await Sales(context).PurchaseAsync(Cart(seed, method), seed.Employee, default)).Id;
            var refund = new RefundPurchase(Guid.NewGuid(), seed.Shift, "Returned unopened");
            await using var first = db.CreateContext();
            await using var second = db.CreateContext();
            var results = await Task.WhenAll(Sales(first).RefundAsync(saleId, refund, seed.Employee, default), Sales(second).RefundAsync(saleId, refund, seed.Employee, default));
            Assert.Equal(results[0].Id, results[1].Id);
            await using var third = db.CreateContext();
            await Assert.ThrowsAsync<ClubException>(() => Sales(third).RefundAsync(saleId, refund with { OperationId = Guid.NewGuid() }, seed.Employee, default));
        }
        await using var verify = db.CreateContext();
        Assert.Equal(10, (await verify.Set<Product>().SingleAsync()).StockQuantity);
        Assert.Equal(100m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(3, await verify.Set<SaleRefund>().CountAsync());
        Assert.Equal(4, await verify.Set<Payment>().CountAsync());
        Assert.Equal(0m, await verify.Set<Payment>().SumAsync(x => x.Amount));
        Assert.Equal(0m, await verify.Set<WalletTransaction>().SumAsync(x => x.Amount));
    }

    [PostgresFact]
    public async Task ShiftOwnershipAndClosure_BlockPurchases()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        Guid other;
        await using (var context = db.CreateContext())
        {
            var employee = new Employee(Guid.NewGuid(), "other", EmployeeRole.Operator, Clock.GetUtcNow().UtcDateTime);
            other = employee.Id; context.Add(employee); await context.SaveChangesAsync();
        }
        await using (var context = db.CreateContext())
            await Assert.ThrowsAsync<ClubException>(() => Sales(context).PurchaseAsync(Cart(seed, PaymentMethod.Cash), other, default));
        await using (var context = db.CreateContext())
            await new ShiftService(new ClubData(context), Clock).CloseAsync(seed.Shift, Guid.NewGuid(), 0m, seed.Employee, default);
        await using (var context = db.CreateContext())
            await Assert.ThrowsAsync<ClubException>(() => Sales(context).PurchaseAsync(Cart(seed, PaymentMethod.Cash), seed.Employee, default));
        await using var verify = db.CreateContext();
        Assert.Empty(await verify.Set<Sale>().ToListAsync());
    }

    [PostgresFact]
    public async Task ConcurrentOpening_OneOpenShiftPerEmployeeAndCloseReplayIsStable()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        await using (var context = db.CreateContext())
            await new ShiftService(new ClubData(context), Clock).CloseAsync(seed.Shift, Guid.NewGuid(), 0m, seed.Employee, default);
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        async Task<bool> Open(GameClubDbContext context)
        {
            try { await new ShiftService(new ClubData(context), Clock).OpenAsync(Guid.NewGuid(), 100m, seed.Employee, default); return true; }
            catch (ClubException) { return false; }
        }
        var outcomes = await Task.WhenAll(Open(first), Open(second));
        Assert.Single(outcomes, x => x);
        await using var verify = db.CreateContext();
        Assert.Single(await verify.Set<EmployeeShift>().Where(s => s.ClosedAtUtc == null).ToListAsync());
    }

    [PostgresFact]
    public async Task ShiftTotals_IncludeSalesRefundsAndCashDifference_AndFreezeOnClose()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        Guid cashSale;
        await using (var context = db.CreateContext()) cashSale = (await Sales(context).PurchaseAsync(Cart(seed, PaymentMethod.Cash), seed.Employee, default)).Id;
        foreach (var method in new[] { PaymentMethod.Card, PaymentMethod.Wallet })
        {
            await using var context = db.CreateContext();
            await Sales(context).PurchaseAsync(Cart(seed, method), seed.Employee, default);
        }
        await using (var context = db.CreateContext())
            await Sales(context).RefundAsync(cashSale, new(Guid.NewGuid(), seed.Shift, "Returned"), seed.Employee, default);
        var closeId = Guid.NewGuid();
        await using (var context = db.CreateContext())
            await new ShiftService(new ClubData(context), Clock).CloseAsync(seed.Shift, closeId, 2m, seed.Employee, default);
        await using (var context = db.CreateContext())
        {
            var service = new ShiftService(new ClubData(context), Clock);
            await service.CloseAsync(seed.Shift, closeId, 2m, seed.Employee, default);
            var summary = await service.SummaryAsync(seed.Shift, seed.Employee, false, default);
            Assert.Equal(30m, summary.ProductSales);
            Assert.Equal(10m, summary.Refunds);
            Assert.Equal(0m, summary.Cash);
            Assert.Equal(10m, summary.Card);
            Assert.Equal(10m, summary.Wallet);
            Assert.Equal(0m, summary.ExpectedCash);
            Assert.Equal(2m, summary.CashDifference);
        }
        await using var mismatchContext = db.CreateContext();
        var error = await Assert.ThrowsAsync<ClubException>(() => new ShiftService(new ClubData(mismatchContext), Clock)
            .CloseAsync(seed.Shift, closeId, 3m, seed.Employee, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
    }

    [PostgresFact]
    public async Task GamingTotals_AttributeOnlyActorWithinShift_AndRemainFrozenAfterClosure()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        var now = Clock.GetUtcNow().UtcDateTime;
        await using (var context = db.CreateContext())
        {
            var wallet = await context.Set<Wallet>().SingleAsync();
            context.AddRange(
                new WalletTransaction(Guid.NewGuid(), wallet.Id, WalletTransactionType.GamingCharge, -7m, 93m, now.AddSeconds(1), "Test", null, Guid.NewGuid(), seed.Employee),
                new WalletTransaction(Guid.NewGuid(), wallet.Id, WalletTransactionType.GamingCharge, -9m, 84m, now.AddSeconds(2), "Test", null, Guid.NewGuid(), null),
                new WalletTransaction(Guid.NewGuid(), wallet.Id, WalletTransactionType.GamingCharge, -11m, 73m, now.AddSeconds(-1), "Test", null, Guid.NewGuid(), seed.Employee));
            await context.SaveChangesAsync();
        }
        var closingClock = new AtClock(now.AddMinutes(1));
        await using (var context = db.CreateContext())
        {
            var summary = await new ShiftService(new ClubData(context), closingClock).CloseAsync(seed.Shift, Guid.NewGuid(), 0m, seed.Employee, default);
            Assert.Equal(7m, summary.GamingSales);
            Assert.Equal(7m, summary.Wallet);
        }
        await using (var context = db.CreateContext())
        {
            var wallet = await context.Set<Wallet>().SingleAsync();
            context.Add(new WalletTransaction(Guid.NewGuid(), wallet.Id, WalletTransactionType.GamingCharge, -13m, 60m, now.AddMinutes(2), "Test", null, Guid.NewGuid(), seed.Employee));
            await context.SaveChangesAsync();
        }
        await using var verify = db.CreateContext();
        var frozen = await new ShiftService(new ClubData(verify), new AtClock(now.AddHours(1))).SummaryAsync(seed.Shift, seed.Employee, false, default);
        Assert.Equal(7m, frozen.GamingSales);
    }

    [PostgresFact]
    public async Task ConcurrentGamingChargeAndClose_RetriesInsteadOfLosingAnInWindowCharge()
    {
        await using var db = await postgres.CreateDatabaseAsync();
        var seed = await Seed(db);
        var opened = Clock.GetUtcNow().UtcDateTime;
        var chargingClock = new MutableClock(opened.AddSeconds(1));
        await using var chargeContext = db.CreateContext();
        var paused = new PausedSaveData(new ClubData(chargeContext));
        var walletService = new WalletService(paused, chargingClock, new WalletPolicy());
        var operationId = Guid.NewGuid();
        var charging = paused.AtomicAsync(ct => walletService.ApplyCoreAsync(seed.User, -7m,
            WalletTransactionType.GamingCharge, operationId, "Test", null, seed.Employee, ct), default);
        await paused.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var closeContext = db.CreateContext();
            var summary = await new ShiftService(new ClubData(closeContext), new AtClock(opened.AddSeconds(2)))
                .CloseAsync(seed.Shift, Guid.NewGuid(), 0m, seed.Employee, default);
            Assert.Equal(0m, summary.GamingSales);
            chargingClock.Now = opened.AddSeconds(3);
        }
        finally { paused.Release.TrySetResult(); }
        var charge = await charging;
        // The losing charge must be retried with its new time, not committed using the
        // pre-close timestamp omitted from the immutable shift accounting snapshot.
        Assert.Equal(opened.AddSeconds(3), charge.CreatedAtUtc);
        Assert.True(paused.SaveCount >= 2);
        await using var verify = db.CreateContext();
        Assert.Single(await verify.Set<WalletTransaction>().ToListAsync());
        Assert.Equal(93m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        var shift = await verify.Set<EmployeeShift>().SingleAsync();
        Assert.True(charge.CreatedAtUtc >= shift.ClosedAtUtc);
        Assert.Equal(0m, shift.GamingSalesSnapshot);
    }

    private static async Task<bool> Attempt(PosSaleService service, CartPurchase cart, Guid employee)
    {
        try { await service.PurchaseAsync(cart, employee, default); return true; }
        catch (ClubException) { return false; }
    }
    private static CartPurchase Cart(SeedData seed, PaymentMethod method) => new(Guid.NewGuid(), seed.Shift, seed.User, null, method, [new(seed.Product, 1)]);
    private static PosSaleService Sales(GameClubDbContext context, SaveGate? gate = null)
    {
        IClubData data = new ClubData(context);
        if (gate != null) data = new GatedData(data, gate);
        return new(data, new WalletService(data, Clock, new WalletPolicy()), Clock);
    }
    private static async Task<SeedData> Seed(PostgresTestDatabase db, int stock = 10, decimal balance = 100m)
    {
        await using var context = db.CreateContext();
        var now = Clock.GetUtcNow().UtcDateTime;
        var employee = new Employee(Guid.NewGuid(), "operator", EmployeeRole.Operator, now);
        var user = new User(Guid.NewGuid(), "pos_player", null, null, null, now);
        var wallet = new Wallet(Guid.NewGuid(), user.Id, now); wallet.Apply(balance);
        var product = new Product(Guid.NewGuid(), null, "Drink", 10m, stock);
        var shift = new EmployeeShift(Guid.NewGuid(), employee.Id, now, 0m);
        context.AddRange(employee, user, wallet, product, shift);
        await context.SaveChangesAsync();
        return new(employee.Id, user.Id, product.Id, shift.Id);
    }
    private sealed record SeedData(Guid Employee, Guid User, Guid Product, Guid Shift);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class AtClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
    private sealed class PausedSaveData(IClubData inner) : IClubData
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCount { get; private set; }
        public IQueryable<T> Query<T>() where T : class => inner.Query<T>();
        public void Add<T>(T entity) where T : class => inner.Add(entity);
        public async Task SaveAsync(CancellationToken ct)
        {
            if (++SaveCount == 1)
            {
                Arrived.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            await inner.SaveAsync(ct);
        }
        public Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => inner.AtomicAsync(action, ct);
    }
    private sealed class SaveGate
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public async Task Arrive(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }
    private sealed class GatedData(IClubData inner, SaveGate gate) : IClubData
    {
        private int _saves;
        public IQueryable<T> Query<T>() where T : class => inner.Query<T>();
        public void Add<T>(T entity) where T : class => inner.Add(entity);
        public async Task SaveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _saves) == 1) await gate.Arrive(ct);
            await inner.SaveAsync(ct);
        }
        public Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => inner.AtomicAsync(action, ct);
    }
}
