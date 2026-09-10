using GameClub.Application.Abstractions;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Pos;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Billing;

public sealed class WalletPolicy
{
    public bool AllowNegativeBalance { get; set; }
}

/// <summary>Core methods require the caller's AtomicAsync unit of work; they never start nested transactions.</summary>
public sealed class WalletService(IClubData data, TimeProvider clock, WalletPolicy policy)
{
    public Task<WalletTransaction> DepositAsync(Guid userId, decimal amount, Guid operationId,
        Guid? employeeId, CancellationToken ct) => AtomicWithConflictRetryAsync(token =>
            ApplyCoreAsync(userId, amount, WalletTransactionType.Deposit, operationId, "Deposit", null, employeeId, token), ct);

    public Task<WalletTransaction> AdjustmentAsync(Guid userId, decimal signedAmount, Guid operationId,
        Guid? employeeId, CancellationToken ct) => AtomicWithConflictRetryAsync(token =>
            ApplyCoreAsync(userId, signedAmount, WalletTransactionType.Adjustment, operationId, "Adjustment", null, employeeId, token), ct);

    public async Task<Wallet> GetOrCreateCoreAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) throw new ClubException("USER_NOT_FOUND");
        var existing = await data.Query<Wallet>().SingleOrDefaultAsync(w => w.UserId == userId, ct);
        if (existing is not null) return existing;
        if (!await data.Query<User>().AnyAsync(u => u.Id == userId, ct)) throw new ClubException("USER_NOT_FOUND");
        var wallet = new Wallet(Guid.NewGuid(), userId, clock.GetUtcNow().UtcDateTime);
        data.Add(wallet);
        // Persist within the caller's transaction so subsequent core queries see the new wallet.
        await data.SaveAsync(ct);
        return wallet;
    }

    public async Task<decimal> GetAvailableCoreAsync(Wallet wallet, CancellationToken ct)
    {
        var held = await data.Query<WalletReservation>().Where(r => r.WalletId == wallet.Id && r.ReleasedAtUtc == null)
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        return wallet.Balance - held;
    }

    public async Task EnsureAvailableCoreAsync(Wallet wallet, decimal amount, CancellationToken ct)
    {
        RequireNonNegativeAmount(amount);
        if (!policy.AllowNegativeBalance && await GetAvailableCoreAsync(wallet, ct) < amount)
            throw new ClubException("INSUFFICIENT_FUNDS");
    }

    public async Task<WalletTransaction> ApplyCoreAsync(Guid userId, decimal signedAmount,
        WalletTransactionType type, Guid operationId, string referenceType, Guid? referenceId,
        Guid? employeeId, CancellationToken ct)
    {
        ValidateInput(signedAmount, type, operationId, referenceType, referenceId, employeeId);
        referenceType = referenceType.Trim();
        var posOperation = await data.Query<PosOperation>().SingleOrDefaultAsync(o => o.Id == operationId, ct);
        if (posOperation is null && (type == WalletTransactionType.ProductPurchase || referenceType is "Sale" or "SaleRefund"))
            throw new ClubException("IDEMPOTENCY_CONFLICT");
        if (posOperation is not null)
        {
            var permitted = false;
            if (posOperation.Kind == "Sale" && type == WalletTransactionType.ProductPurchase &&
                referenceType == "Sale" && referenceId == posOperation.EntityId)
                permitted = await data.Query<Sale>().AnyAsync(s => s.Id == posOperation.EntityId &&
                    s.UserId == userId && s.EmployeeId == employeeId && s.PaymentMethod == PaymentMethod.Wallet &&
                    s.CapturedTotal == -signedAmount, ct);
            if (posOperation.Kind == "Refund" && type == WalletTransactionType.Refund &&
                referenceType == "SaleRefund" && referenceId == posOperation.EntityId)
                permitted = await (from refund in data.Query<SaleRefund>()
                    join sale in data.Query<Sale>() on refund.SaleId equals sale.Id
                    where refund.Id == posOperation.EntityId && refund.EmployeeId == employeeId &&
                        refund.Amount == signedAmount && sale.UserId == userId && sale.PaymentMethod == PaymentMethod.Wallet
                    select refund.Id).AnyAsync(ct);
            if (!permitted) throw new ClubException("IDEMPOTENCY_CONFLICT");
        }
        var sessionOwner = await data.Query<GamingSession>().Where(s => s.Id == operationId)
            .Select(s => (Guid?)s.UserId).SingleOrDefaultAsync(ct);
        if (sessionOwner is not null && (type != WalletTransactionType.GamingCharge ||
            referenceType != "GamingSession" || referenceId != operationId || sessionOwner != userId))
            throw new ClubException("IDEMPOTENCY_CONFLICT");
        var sessionOperation = await data.Query<SessionOperation>().SingleOrDefaultAsync(o => o.Id == operationId, ct);
        if (sessionOperation is not null && (sessionOperation.Type != SessionOperationType.Extend ||
            type != WalletTransactionType.GamingCharge || referenceType != "SessionExtension" ||
            referenceId != sessionOperation.GamingSessionId || employeeId != sessionOperation.EmployeeId ||
            !await data.Query<GamingSession>().AnyAsync(s => s.Id == sessionOperation.GamingSessionId && s.UserId == userId, ct)))
            throw new ClubException("IDEMPOTENCY_CONFLICT");
        var existing = await data.Query<WalletTransaction>().SingleOrDefaultAsync(t => t.OperationId == operationId, ct);
        if (existing is not null)
        {
            var sameUser = await data.Query<Wallet>().AnyAsync(w => w.Id == existing.WalletId && w.UserId == userId, ct);
            if (!sameUser || existing.Amount != signedAmount || existing.Type != type || existing.ReferenceType != referenceType ||
                existing.ReferenceId != referenceId || existing.EmployeeId != employeeId)
                throw new ClubException("IDEMPOTENCY_CONFLICT");
            return existing;
        }

        var wallet = await GetOrCreateCoreAsync(userId, ct);
        if (signedAmount < 0) await EnsureAvailableCoreAsync(wallet, -signedAmount, ct);
        if (type == WalletTransactionType.GamingCharge && employeeId is { } actor)
        {
            // Pair this read with ShiftService.Close's ledger read and shift write. Under
            // SERIALIZABLE a concurrent close/charge must retry, rather than freezing a
            // total which silently omits a charge timestamped inside the closed shift.
            await data.Query<EmployeeShift>().Where(s => s.EmployeeId == actor && s.ClosedAtUtc == null)
                .Select(s => s.Id).SingleOrDefaultAsync(ct);
        }
        try { wallet.Apply(signedAmount, policy.AllowNegativeBalance); }
        catch (ArgumentOutOfRangeException) { throw new ClubException("INVALID_AMOUNT"); }
        catch (InvalidOperationException) { throw new ClubException("INSUFFICIENT_FUNDS"); }
        var transaction = new WalletTransaction(Guid.NewGuid(), wallet.Id, type, signedAmount, wallet.Balance,
            clock.GetUtcNow().UtcDateTime, referenceType, referenceId, operationId, employeeId);
        data.Add(transaction);
        await data.SaveAsync(ct);
        return transaction;
    }

    public async Task<WalletReservation> ReserveCoreAsync(Guid userId, Guid sessionId, decimal amount, CancellationToken ct)
    {
        RequireNonNegativeAmount(amount);
        if (sessionId == Guid.Empty) throw new ClubException("SESSION_NOT_FOUND");
        var wallet = await GetOrCreateCoreAsync(userId, ct);
        var existing = await data.Query<WalletReservation>().SingleOrDefaultAsync(r => r.GamingSessionId == sessionId, ct);
        if (existing is not null)
        {
            if (existing.WalletId != wallet.Id || existing.Amount != amount || existing.ReleasedAtUtc is not null)
                throw new ClubException("RESERVATION_CONFLICT");
            return existing;
        }
        // SaveAsync also makes a newly added GamingSession visible before checking its ownership.
        await data.SaveAsync(ct);
        if (!await data.Query<GamingSession>().AnyAsync(s => s.Id == sessionId && s.UserId == userId, ct))
            throw new ClubException("SESSION_NOT_FOUND");
        await EnsureAvailableCoreAsync(wallet, amount, ct);
        var reservation = new WalletReservation(Guid.NewGuid(), wallet.Id, sessionId, amount, clock.GetUtcNow().UtcDateTime);
        data.Add(reservation);
        await data.SaveAsync(ct);
        return reservation;
    }

    public async Task<WalletTransaction> SettleCoreAsync(Guid sessionId, decimal charge, Guid? employeeId, CancellationToken ct)
    {
        RequireNonNegativeAmount(charge);
        var reservation = await data.Query<WalletReservation>().SingleOrDefaultAsync(r => r.GamingSessionId == sessionId, ct)
            ?? throw new ClubException("WALLET_RESERVATION_NOT_FOUND");
        if (charge > reservation.Amount) throw new ClubException("BILLING_LIMIT_EXCEEDED");
        var wallet = await data.Query<Wallet>().SingleAsync(w => w.Id == reservation.WalletId, ct);
        var settled = await data.Query<WalletTransaction>().SingleOrDefaultAsync(t => t.OperationId == sessionId, ct);
        if (settled is not null)
            return await ApplyCoreAsync(wallet.UserId, -charge, WalletTransactionType.GamingCharge, sessionId,
                "GamingSession", sessionId, employeeId, ct);
        if (!reservation.Release(clock.GetUtcNow().UtcDateTime)) throw new ClubException("RESERVATION_ALREADY_RELEASED");
        // A released reservation must no longer reduce available funds when its final charge is checked.
        // Both this write and the ledger mutation remain inside the same outer transaction.
        await data.SaveAsync(ct);
        return await ApplyCoreAsync(wallet.UserId, -charge, WalletTransactionType.GamingCharge, sessionId,
            "GamingSession", sessionId, employeeId, ct);
    }

    public async Task IncreaseReservationCoreAsync(Guid sessionId, decimal additionalAmount, CancellationToken ct)
    {
        RequireNonNegativeAmount(additionalAmount);
        var reservation = await data.Query<WalletReservation>().SingleOrDefaultAsync(r => r.GamingSessionId == sessionId, ct)
            ?? throw new ClubException("WALLET_RESERVATION_NOT_FOUND");
        if (reservation.ReleasedAtUtc is not null) throw new ClubException("RESERVATION_ALREADY_RELEASED");
        var wallet = await data.Query<Wallet>().SingleAsync(w => w.Id == reservation.WalletId, ct);
        await EnsureAvailableCoreAsync(wallet, additionalAmount, ct);
        try { reservation.Increase(additionalAmount); }
        catch (ArgumentOutOfRangeException) { throw new ClubException("INVALID_AMOUNT"); }
        await data.SaveAsync(ct);
    }

    private async Task<T> AtomicWithConflictRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await data.AtomicAsync(operation, ct); }
            catch (ClubException ex) when (ex.Code == "CONCURRENT_CONFLICT" && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
        }
    }

    private static void ValidateInput(decimal amount, WalletTransactionType type, Guid operationId,
        string referenceType, Guid? referenceId, Guid? employeeId)
    {
        try { WalletTransaction.ValidateAmount(type, amount); }
        catch (ArgumentOutOfRangeException) { throw new ClubException("INVALID_AMOUNT"); }
        if (operationId == Guid.Empty || referenceId == Guid.Empty || employeeId == Guid.Empty)
            throw new ClubException("INVALID_OPERATION_ID");
        if (string.IsNullOrWhiteSpace(referenceType) || referenceType.Trim().Length > WalletTransaction.MaximumReferenceTypeLength)
            throw new ClubException("INVALID_REFERENCE");
    }

    private static void RequireNonNegativeAmount(decimal amount)
    {
        try { Money.RequireExactScale(amount); }
        catch (ArgumentOutOfRangeException) { throw new ClubException("INVALID_AMOUNT"); }
        if (amount < 0) throw new ClubException("INVALID_AMOUNT");
    }
}
