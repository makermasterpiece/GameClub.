using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class WalletIntegrationTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task ConcurrentDistinctDeposits_PreserveBothAmountsWithoutLostUpdate()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var userId = await SeedUserAsync(database, createWallet: false);
        var gate = new SaveGate();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = CreateService(new GatedData(new ClubData(firstContext), gate));
        var second = CreateService(new GatedData(new ClubData(secondContext), gate));
        await Task.WhenAll(first.DepositAsync(userId, 50m, Guid.NewGuid(), null, default),
            second.DepositAsync(userId, 70m, Guid.NewGuid(), null, default));

        await using var verify = database.CreateContext();
        Assert.Equal(120m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(2, await verify.Set<WalletTransaction>().CountAsync());
        Assert.Equal(120m, await verify.Set<WalletTransaction>().SumAsync(t => t.Amount));
        Assert.Contains(await verify.Set<WalletTransaction>().ToListAsync(), t => t.BalanceAfter == 120m);
    }

    [PostgresFact]
    public async Task ConcurrentDuplicateDeposit_ReturnsSameImmutableTransactionOnlyOnce()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var userId = await SeedUserAsync(database);
        var operationId = Guid.NewGuid();
        var gate = new SaveGate();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = CreateService(new GatedData(new ClubData(firstContext), gate));
        var second = CreateService(new GatedData(new ClubData(secondContext), gate));
        var results = await Task.WhenAll(first.DepositAsync(userId, 50m, operationId, null, default),
            second.DepositAsync(userId, 50m, operationId, null, default));

        Assert.Equal(results[0].Id, results[1].Id);
        await using var verify = database.CreateContext();
        Assert.Equal(50m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Single(await verify.Set<WalletTransaction>().ToListAsync());
        var mismatch = await Assert.ThrowsAsync<ClubException>(() => first.DepositAsync(userId, 51m, operationId, null, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", mismatch.Code);
    }

    [PostgresFact]
    public async Task Reservation_ProtectsFundsAndSettlementReleasesUnusedAmountIdempotently()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var userId = await SeedUserAsync(database);
        var sessionId = await SeedSessionAsync(database, userId);
        await using var context = database.CreateContext();
        var data = new ClubData(context);
        var service = CreateService(data);
        await service.DepositAsync(userId, 100m, Guid.NewGuid(), null, default);
        await data.AtomicAsync(token => service.ReserveCoreAsync(userId, sessionId, 30m, token), default);
        var available = await data.AtomicAsync(async token =>
            await service.GetAvailableCoreAsync(await service.GetOrCreateCoreAsync(userId, token), token), default);
        Assert.Equal(70m, available);

        var debit = await Assert.ThrowsAsync<ClubException>(() => service.AdjustmentAsync(userId, -71m, Guid.NewGuid(), null, default));
        Assert.Equal("INSUFFICIENT_FUNDS", debit.Code);
        var excessiveCharge = await Assert.ThrowsAsync<ClubException>(() =>
            data.AtomicAsync(token => service.SettleCoreAsync(sessionId, 31m, null, token), default));
        Assert.Equal("BILLING_LIMIT_EXCEEDED", excessiveCharge.Code);
        await using (var held = database.CreateContext())
        {
            Assert.Null((await held.Set<WalletReservation>().SingleAsync()).ReleasedAtUtc);
            Assert.Equal(100m, (await held.Set<Wallet>().SingleAsync()).Balance);
        }

        var charge = await data.AtomicAsync(token => service.SettleCoreAsync(sessionId, 20m, null, token), default);
        var repeated = await data.AtomicAsync(token => service.SettleCoreAsync(sessionId, 20m, null, token), default);
        Assert.Equal(charge.Id, repeated.Id);
        Assert.Equal(-20m, charge.Amount);
        Assert.Equal(80m, charge.BalanceAfter);
        var altered = await Assert.ThrowsAsync<ClubException>(() =>
            data.AtomicAsync(token => service.SettleCoreAsync(sessionId, 21m, null, token), default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", altered.Code);
        await using var verify = database.CreateContext();
        Assert.Equal(80m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.NotNull((await verify.Set<WalletReservation>().SingleAsync()).ReleasedAtUtc);
        Assert.Equal(2, await verify.Set<WalletTransaction>().CountAsync());
    }

    [PostgresFact]
    public async Task FailedLedgerInsert_RollsBackBalanceAndReservationReleaseTogether()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var userId = await SeedUserAsync(database);
        var sessionId = await SeedSessionAsync(database, userId);
        await using var context = database.CreateContext();
        var data = new ClubData(context);
        var service = CreateService(data);
        await service.DepositAsync(userId, 100m, Guid.NewGuid(), null, default);
        await data.AtomicAsync(token => service.ReserveCoreAsync(userId, sessionId, 30m, token), default);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION gameclub_test_reject_wallet_transaction() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Integration test ledger write failure'; END $$;
            CREATE TRIGGER gameclub_test_reject_wallet_transaction
            BEFORE INSERT ON wallet_transactions FOR EACH ROW
            EXECUTE FUNCTION gameclub_test_reject_wallet_transaction();
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            data.AtomicAsync(token => service.SettleCoreAsync(sessionId, 20m, null, token), default));
        await using var verify = database.CreateContext();
        Assert.Equal(100m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Null((await verify.Set<WalletReservation>().SingleAsync()).ReleasedAtUtc);
        Assert.Single(await verify.Set<WalletTransaction>().ToListAsync());
    }

    [PostgresFact]
    public async Task InvalidAmountsAndCrossUserReplayAreRejected_NegativeBalanceNeedsExplicitPolicy()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var userId = await SeedUserAsync(database);
        var otherUserId = await SeedUserAsync(database);
        await using var context = database.CreateContext();
        var data = new ClubData(context);
        var service = CreateService(data);
        var scale = await Assert.ThrowsAsync<ClubException>(() => service.DepositAsync(userId, 0.001m, Guid.NewGuid(), null, default));
        Assert.Equal("INVALID_AMOUNT", scale.Code);
        var negativeDeposit = await Assert.ThrowsAsync<ClubException>(() => service.DepositAsync(userId, -1m, Guid.NewGuid(), null, default));
        Assert.Equal("INVALID_AMOUNT", negativeDeposit.Code);
        var denied = await Assert.ThrowsAsync<ClubException>(() => service.AdjustmentAsync(userId, -1m, Guid.NewGuid(), null, default));
        Assert.Equal("INSUFFICIENT_FUNDS", denied.Code);
        var operationId = Guid.NewGuid();
        await service.DepositAsync(userId, 1m, operationId, null, default);
        var crossUser = await Assert.ThrowsAsync<ClubException>(() => service.DepositAsync(otherUserId, 1m, operationId, null, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", crossUser.Code);
        var creditEnabled = new WalletService(data, Clock, new WalletPolicy { AllowNegativeBalance = true });
        var credit = await creditEnabled.AdjustmentAsync(userId, -2m, Guid.NewGuid(), null, default);
        Assert.Equal(-1m, credit.BalanceAfter);
        await using var verify = database.CreateContext();
        Assert.Equal(-1m, (await verify.Set<Wallet>().SingleAsync(w => w.UserId == userId)).Balance);
        Assert.Equal(0m, (await verify.Set<Wallet>().SingleAsync(w => w.UserId == otherUserId)).Balance);
        Assert.Equal(2, await verify.Set<WalletTransaction>().CountAsync());
    }

    private static readonly TimeProvider Clock = new FixedClock();
    private static WalletService CreateService(IClubData data) => new(data, Clock, new WalletPolicy());

    private static async Task<Guid> SeedUserAsync(PostgresTestDatabase database, bool createWallet = true)
    {
        await using var context = database.CreateContext();
        var user = new User(Guid.NewGuid(), "wallet_" + Guid.NewGuid().ToString("N")[..12], null, null, null, Clock.GetUtcNow().UtcDateTime);
        user.SetPasswordHash(new PasswordHasher<User>().HashPassword(user, "IntegrationOnly!234"));
        context.Users.Add(user);
        if (createWallet) context.Set<Wallet>().Add(new Wallet(Guid.NewGuid(), user.Id, Clock.GetUtcNow().UtcDateTime));
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> SeedSessionAsync(PostgresTestDatabase database, Guid userId)
    {
        await using var context = database.CreateContext();
        var now = Clock.GetUtcNow().UtcDateTime;
        var station = new Station(Guid.NewGuid(), "Wallet test", "WALLET-" + Guid.NewGuid().ToString("N"), "127.0.0.1", "0.1.0", now);
        var session = new GamingSession(Guid.NewGuid(), userId, station.Id, 30, now);
        session.Start(now);
        context.Stations.Add(station);
        context.Set<GamingSession>().Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class SaveGate
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class GatedData(IClubData inner, SaveGate gate) : IClubData
    {
        private int _saveCount;
        public IQueryable<T> Query<T>() where T : class => inner.Query<T>();
        public void Add<T>(T entity) where T : class => inner.Add(entity);
        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _saveCount) == 1) await gate.ArriveAsync(cancellationToken);
            await inner.SaveAsync(cancellationToken);
        }
        public Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            inner.AtomicAsync(operation, cancellationToken);
    }
}
