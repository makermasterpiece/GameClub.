using System.Security.Cryptography;
using System.Text.Json;
using GameClub.Application.Abstractions;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Pos;
using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Pos;

public sealed class ProductCatalogService(IClubData data, TimeProvider clock)
{
    public Task<List<ProductCategory>> ListCategoriesAsync(CancellationToken ct) => data.Query<ProductCategory>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    public Task<List<Product>> ListProductsAsync(CancellationToken ct) => data.Query<Product>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<ProductCategory> SaveCategoryAsync(Guid? id, string name, bool isActive, Guid employeeId, CancellationToken ct) => data.AtomicAsync(async token =>
    {
        RequireEmployee(employeeId);
        var category = new ProductCategory(id ?? Guid.NewGuid(), name, isActive);
        if (id == null)
        {
            if (await data.Query<ProductCategory>().CountAsync(token) >= 1000) throw new ClubException("POS_CATALOG_LIMIT");
            data.Add(category);
        }
        else { category = await data.Query<ProductCategory>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new ClubException("PRODUCT_CATEGORY_NOT_FOUND"); category.Update(name, isActive); }
        Audit("ProductCategorySaved", category.Id, employeeId);
        return category;
    }, ct);

    public Task<Product> SaveProductAsync(Guid? id, Guid? categoryId, string name, decimal price, int initialStock, bool isActive, Guid employeeId, CancellationToken ct) => data.AtomicAsync(async token =>
    {
        RequireEmployee(employeeId);
        if (id != null && initialStock != 0) throw new ClubException("USE_STOCK_ADJUSTMENT");
        if (categoryId != null && !await data.Query<ProductCategory>().AnyAsync(x => x.Id == categoryId, token)) throw new ClubException("PRODUCT_CATEGORY_NOT_FOUND");
        var product = new Product(id ?? Guid.NewGuid(), categoryId, name, price, initialStock, isActive);
        if (id == null)
        {
            if (await data.Query<Product>().CountAsync(token) >= 1000) throw new ClubException("POS_CATALOG_LIMIT");
            data.Add(product);
            if (initialStock > 0) data.Add(new StockMovement(Guid.NewGuid(), product.Id, initialStock, "Initial stock", employeeId, clock.GetUtcNow().UtcDateTime));
        }
        else { product = await data.Query<Product>().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new ClubException("PRODUCT_NOT_FOUND"); product.Update(categoryId, name, price, isActive); }
        Audit("ProductSaved", product.Id, employeeId);
        return product;
    }, ct);

    public Task<Product> AdjustStockAsync(Guid productId, Guid operationId, int delta, string reason, Guid employeeId, CancellationToken ct) => PosAtomic.RunAsync(data, async token =>
    {
        RequireEmployee(employeeId);
        var movement = new StockMovement(operationId, productId, delta, reason, employeeId, clock.GetUtcNow().UtcDateTime);
        var fingerprint = PosFingerprint.Create(new { productId, delta, movement.Reason, employeeId });
        var previous = await data.Query<PosOperation>().SingleOrDefaultAsync(x => x.Id == operationId, token);
        if (previous != null && (previous.Kind != "StockAdjustment" || previous.Fingerprint != fingerprint)) throw new ClubException("IDEMPOTENCY_CONFLICT");
        var product = await data.Query<Product>().SingleOrDefaultAsync(x => x.Id == productId, token) ?? throw new ClubException("PRODUCT_NOT_FOUND");
        if (previous != null) return product;
        if (await data.Query<WalletTransaction>().AnyAsync(x => x.OperationId == operationId, token) ||
            await data.Query<GamingSession>().AnyAsync(x => x.Id == operationId, token) ||
            await data.Query<SessionOperation>().AnyAsync(x => x.Id == operationId, token)) throw new ClubException("IDEMPOTENCY_CONFLICT");
        if ((long)product.StockQuantity + delta is < 0 or > Product.MaximumStock) throw new ClubException("INVALID_STOCK_QUANTITY");
        product.AdjustStock(delta);
        data.Add(movement); data.Add(new PosOperation(operationId, "StockAdjustment", fingerprint, productId));
        Audit("ProductStockAdjusted", productId, employeeId);
        return product;
    }, ct);

    private static void RequireEmployee(Guid id) { if (id == Guid.Empty) throw new ClubException("EMPLOYEE_REQUIRED"); }
    private void Audit(string type, Guid id, Guid employee) => data.Add(new SecurityAuditEvent(Guid.NewGuid(), type, null, clock.GetUtcNow().UtcDateTime, null, $"EntityId={id:D};EmployeeId={employee:D}"));
}

internal static class PosFingerprint
{
    public static string Create<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

internal static class PosAtomic
{
    public static async Task<T> RunAsync<T>(IClubData data, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await data.AtomicAsync(action, ct); }
            catch (ClubException ex) when (ex.Code == "CONCURRENT_CONFLICT" && attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct); }
        }
    }
}
