using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Domain.Billing;
using GameClub.Domain.Employees;
using GameClub.Domain.Gaming;
using GameClub.Domain.Pos;
using GameClub.Domain.Security;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Pos;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartItem(Guid ProductId, int Quantity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartPurchase(Guid OperationId, Guid ShiftId, Guid? UserId, Guid? StationId,
    [property: JsonRequired] PaymentMethod PaymentMethod, IReadOnlyList<CartItem> Items, decimal? ExpectedTotal = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundPurchase(Guid OperationId, Guid ShiftId, string Reason);

/// <summary>Completed sales are immutable; corrections are separate, full-refund records.</summary>
public sealed class PosSaleService(IClubData data, WalletService wallets, TimeProvider clock)
{
    public Task<Sale> PurchaseAsync(CartPurchase purchase, Guid employeeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        RequireIds(purchase.OperationId, purchase.ShiftId, employeeId);
        if (!Enum.IsDefined(purchase.PaymentMethod) || purchase.UserId == Guid.Empty || purchase.StationId == Guid.Empty)
            throw new ClubException("INVALID_PURCHASE");
        if (purchase.ExpectedTotal is { } expected)
        {
            try { Money.RequireExactScale(expected); }
            catch (ArgumentOutOfRangeException) { throw new ClubException("INVALID_AMOUNT"); }
            if (expected <= 0) throw new ClubException("INVALID_AMOUNT");
        }
        if (purchase.Items is null || purchase.Items.Count is < 1 or > 100 ||
            purchase.Items.Any(i => i is null || i.ProductId == Guid.Empty || i.Quantity is < 1 or > 1000) ||
            purchase.Items.Select(i => i.ProductId).Distinct().Count() != purchase.Items.Count)
            throw new ClubException("INVALID_CART");
        var items = purchase.Items.OrderBy(i => i.ProductId).ToArray();
        var fingerprint = Fingerprint(new { employeeId, purchase.ShiftId, purchase.UserId,
            purchase.StationId, purchase.PaymentMethod, purchase.ExpectedTotal, Items = items });
        return AtomicAsync(async token =>
        {
            var replay = await ReplayAsync(purchase.OperationId, "Sale", fingerprint, token);
            if (replay is not null)
                return await data.Query<Sale>().SingleAsync(s => s.Id == replay.EntityId, token);
            var now = clock.GetUtcNow().UtcDateTime;
            await RequireShiftAsync(purchase.ShiftId, employeeId, token);
            if (purchase.PaymentMethod == PaymentMethod.Wallet && purchase.UserId is null)
                throw new ClubException("USER_REQUIRED");
            if (purchase.UserId is { } userId && !await data.Query<User>()
                .AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, token))
                throw new ClubException("USER_NOT_FOUND");
            if (purchase.StationId is { } stationId && (purchase.UserId is null ||
                !await data.Query<PlayerAuthSession>().AnyAsync(a => a.StationId == stationId &&
                    a.UserId == purchase.UserId && a.Status == PlayerAuthSessionStatus.Active && a.ExpiresAtUtc > now, token)))
                throw new ClubException("PLAYER_NOT_AUTHENTICATED");
            var ids = items.Select(i => i.ProductId).ToArray();
            var products = await data.Query<Product>().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, token);
            var categoryIds = products.Values.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).Distinct().ToArray();
            var activeCategories = (await data.Query<ProductCategory>().Where(c => categoryIds.Contains(c.Id) && c.IsActive)
                .Select(c => c.Id).ToListAsync(token)).ToHashSet();
            var lines = new List<SaleItem>();
            decimal total = 0;
            foreach (var item in items)
            {
                if (!products.TryGetValue(item.ProductId, out var product) || !product.IsActive ||
                    product.CategoryId is { } categoryId && !activeCategories.Contains(categoryId))
                    throw new ClubException("PRODUCT_NOT_AVAILABLE");
                if (product.StockQuantity < item.Quantity) throw new ClubException("INSUFFICIENT_STOCK");
                SaleItem line;
                try
                {
                    line = new SaleItem(Guid.NewGuid(), purchase.OperationId, product.Id, product.Name, product.Price, item.Quantity);
                    total = Money.RequireExactScale(checked(total + line.LineTotal));
                }
                catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
                { throw new ClubException("INVALID_AMOUNT"); }
                lines.Add(line);
            }
            if (purchase.ExpectedTotal is { } expectedTotal && expectedTotal != total)
                throw new ClubException("PRICE_CHANGED");
            foreach (var item in items)
            {
                products[item.ProductId].AdjustStock(-item.Quantity);
                data.Add(new StockMovement(Guid.NewGuid(), item.ProductId, -item.Quantity,
                    $"Sale:{purchase.OperationId:D}", employeeId, now));
            }
            var sale = new Sale(purchase.OperationId, purchase.ShiftId, employeeId, purchase.UserId,
                purchase.StationId, purchase.PaymentMethod, total, now, fingerprint);
            data.Add(sale);
            foreach (var line in lines) data.Add(line);
            data.Add(new PosOperation(purchase.OperationId, "Sale", fingerprint, sale.Id));
            // Make the operation journal visible to the wallet ownership guard, in this same transaction.
            await data.SaveAsync(token);
            if (purchase.PaymentMethod == PaymentMethod.Wallet)
                await wallets.ApplyCoreAsync(purchase.UserId!.Value, -total, WalletTransactionType.ProductPurchase,
                    purchase.OperationId, "Sale", sale.Id, employeeId, token);
            else
                data.Add(new Payment(Guid.NewGuid(), sale.Id, null, purchase.ShiftId, employeeId,
                    purchase.PaymentMethod, total, now));
            data.Add(new SecurityAuditEvent(Guid.NewGuid(), "PosSaleCompleted", purchase.StationId, now,
                null, $"SaleId={sale.Id:D};EmployeeId={employeeId:D};ShiftId={purchase.ShiftId:D}", purchase.UserId));
            await data.SaveAsync(token);
            return sale;
        }, ct);
    }

    public Task<SaleRefund> RefundAsync(Guid saleId, RefundPurchase purchase, Guid employeeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        RequireIds(purchase.OperationId, purchase.ShiftId, employeeId);
        var reason = purchase.Reason?.Trim() ?? string.Empty;
        if (saleId == Guid.Empty || reason.Length is < 1 or > 500) throw new ClubException("INVALID_REFUND");
        var fingerprint = Fingerprint(new { saleId, employeeId, purchase.ShiftId, Reason = reason });
        return AtomicAsync(async token =>
        {
            var replay = await ReplayAsync(purchase.OperationId, "Refund", fingerprint, token);
            if (replay is not null)
                return await data.Query<SaleRefund>().SingleAsync(r => r.Id == replay.EntityId, token);
            await RequireShiftAsync(purchase.ShiftId, employeeId, token);
            var sale = await data.Query<Sale>().SingleOrDefaultAsync(s => s.Id == saleId, token)
                ?? throw new ClubException("SALE_NOT_FOUND");
            if (await data.Query<SaleRefund>().AnyAsync(r => r.SaleId == saleId, token))
                throw new ClubException("SALE_ALREADY_REFUNDED");
            var lines = await data.Query<SaleItem>().Where(i => i.SaleId == saleId).ToListAsync(token);
            var now = clock.GetUtcNow().UtcDateTime;
            var ids = lines.Select(i => i.ProductId).ToArray();
            var products = await data.Query<Product>().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, token);
            foreach (var line in lines)
            {
                if (!products.TryGetValue(line.ProductId, out var product)) throw new ClubException("PRODUCT_NOT_FOUND");
                try { product.AdjustStock(line.Quantity); }
                catch (ArgumentOutOfRangeException) { throw new ClubException("STOCK_LIMIT_EXCEEDED"); }
                data.Add(new StockMovement(Guid.NewGuid(), product.Id, line.Quantity,
                    $"Refund:{purchase.OperationId:D}", employeeId, now));
            }
            var refund = new SaleRefund(purchase.OperationId, sale.Id, purchase.ShiftId, employeeId,
                sale.CapturedTotal, reason, now, fingerprint);
            data.Add(refund);
            data.Add(new PosOperation(purchase.OperationId, "Refund", fingerprint, refund.Id));
            await data.SaveAsync(token);
            if (sale.PaymentMethod == PaymentMethod.Wallet)
                await wallets.ApplyCoreAsync(sale.UserId!.Value, sale.CapturedTotal, WalletTransactionType.Refund,
                    purchase.OperationId, "SaleRefund", refund.Id, employeeId, token);
            else
                data.Add(new Payment(Guid.NewGuid(), sale.Id, refund.Id, purchase.ShiftId, employeeId,
                    sale.PaymentMethod, -sale.CapturedTotal, now));
            data.Add(new SecurityAuditEvent(Guid.NewGuid(), "PosSaleRefunded", sale.StationId, now, null,
                $"SaleId={sale.Id:D};RefundId={refund.Id:D};EmployeeId={employeeId:D};ShiftId={purchase.ShiftId:D}", sale.UserId));
            await data.SaveAsync(token);
            return refund;
        }, ct);
    }

    private async Task RequireShiftAsync(Guid shiftId, Guid employeeId, CancellationToken ct)
    {
        if (!await data.Query<Employee>().AnyAsync(e => e.Id == employeeId && e.IsActive, ct))
            throw new ClubException("EMPLOYEE_NOT_FOUND");
        if (!await data.Query<EmployeeShift>().AnyAsync(s => s.Id == shiftId && s.EmployeeId == employeeId && s.ClosedAtUtc == null, ct))
            throw new ClubException("SHIFT_NOT_OPEN");
    }

    private async Task<PosOperation?> ReplayAsync(Guid id, string kind, string fingerprint, CancellationToken ct)
    {
        var existing = await data.Query<PosOperation>().SingleOrDefaultAsync(o => o.Id == id, ct);
        if (existing is not null)
        {
            if (existing.Kind != kind || existing.Fingerprint != fingerprint) throw new ClubException("IDEMPOTENCY_CONFLICT");
            return existing;
        }
        if (await data.Query<WalletTransaction>().AnyAsync(t => t.OperationId == id, ct) ||
            await data.Query<GamingSession>().AnyAsync(s => s.Id == id, ct) ||
            await data.Query<SessionOperation>().AnyAsync(o => o.Id == id, ct))
            throw new ClubException("IDEMPOTENCY_CONFLICT");
        return null;
    }

    private async Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await data.AtomicAsync(action, ct); }
            catch (ClubException ex) when (ex.Code == "CONCURRENT_CONFLICT" && attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct); }
        }
    }

    private static void RequireIds(Guid operationId, Guid shiftId, Guid employeeId)
    {
        if (operationId == Guid.Empty || shiftId == Guid.Empty || employeeId == Guid.Empty)
            throw new ClubException("INVALID_OPERATION_ID");
    }

    private static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
