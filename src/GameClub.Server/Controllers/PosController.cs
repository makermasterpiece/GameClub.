using System.Text.Json.Serialization;
using GameClub.Application.Abstractions;
using GameClub.Application.Pos;
using GameClub.Domain.Pos;
using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PosCategoryRequest(string Name, [property: JsonRequired] bool IsActive);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PosProductRequest(Guid? CategoryId, string Name, decimal Price, int InitialStock, [property: JsonRequired] bool IsActive);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PosStockRequest(Guid OperationId, int Delta, string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OpenShiftRequest(Guid OperationId, [property: JsonRequired] decimal OpeningCash);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloseShiftRequest(Guid OperationId, [property: JsonRequired] decimal ClosingCash);

[ApiController]
[Route("api/admin/pos")]
[RequestSizeLimit(32 * 1024)]
public sealed class PosController(ProductCatalogService catalog, PosSaleService sales, ShiftService shifts,
    IClubData data) : ControllerBase
{
    private Guid EmployeeId => Guid.Parse(User.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim)!.Value);
    private bool CanReadOther => User.HasClaim(EmployeeAuthenticationDefaults.PermissionClaim, EmployeePermissions.Money);

    [HttpGet("catalog")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Catalog(CancellationToken ct) => Ok(new
    {
        categories = await catalog.ListCategoriesAsync(ct), products = await catalog.ListProductsAsync(ct)
    });

    [HttpPost("categories")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> CreateCategory(PosCategoryRequest request, CancellationToken ct) =>
        Ok(await catalog.SaveCategoryAsync(null, request.Name, request.IsActive, EmployeeId, ct));

    [HttpPut("categories/{id:guid}")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> UpdateCategory(Guid id, PosCategoryRequest request, CancellationToken ct) =>
        Ok(await catalog.SaveCategoryAsync(id, request.Name, request.IsActive, EmployeeId, ct));

    [HttpPost("products")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> CreateProduct(PosProductRequest request, CancellationToken ct) =>
        Ok(await catalog.SaveProductAsync(null, request.CategoryId, request.Name, request.Price, request.InitialStock, request.IsActive, EmployeeId, ct));

    [HttpPut("products/{id:guid}")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> UpdateProduct(Guid id, PosProductRequest request, CancellationToken ct) =>
        Ok(await catalog.SaveProductAsync(id, request.CategoryId, request.Name, request.Price, request.InitialStock, request.IsActive, EmployeeId, ct));

    [HttpPost("products/{id:guid}/stock")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> AdjustStock(Guid id, PosStockRequest request, CancellationToken ct) =>
        Ok(await catalog.AdjustStockAsync(id, request.OperationId, request.Delta, request.Reason, EmployeeId, ct));

    [HttpGet("shifts/current")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> CurrentShift(CancellationToken ct) => Ok(new { shift = await shifts.CurrentAsync(EmployeeId, ct) });

    [HttpGet("shifts")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> ShiftHistory(CancellationToken ct) => Ok(await data.Query<EmployeeShift>().AsNoTracking()
        .Where(s => CanReadOther || s.EmployeeId == EmployeeId).OrderByDescending(s => s.OpenedAtUtc).Take(50).ToListAsync(ct));

    [HttpGet("shifts/{id:guid}")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> ShiftSummary(Guid id, CancellationToken ct) => Ok(await shifts.SummaryAsync(id, EmployeeId, CanReadOther, ct));

    [HttpPost("shifts")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> OpenShift(OpenShiftRequest request, CancellationToken ct) =>
        Ok(await shifts.OpenAsync(request.OperationId, request.OpeningCash, EmployeeId, ct));

    [HttpPost("shifts/{id:guid}/close")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> CloseShift(Guid id, CloseShiftRequest request, CancellationToken ct) =>
        Ok(await shifts.CloseAsync(id, request.OperationId, request.ClosingCash, EmployeeId, ct));

    [HttpPost("sales")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Purchase(CartPurchase request, CancellationToken ct) => Ok(await sales.PurchaseAsync(request, EmployeeId, ct));

    [HttpPost("sales/{id:guid}/refund")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Money)]
    public async Task<IActionResult> Refund(Guid id, RefundPurchase request, CancellationToken ct) => Ok(await sales.RefundAsync(id, request, EmployeeId, ct));

    [HttpGet("sales")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Sales(CancellationToken ct) => Ok(await data.Query<Sale>().AsNoTracking()
        .Where(s => CanReadOther || s.EmployeeId == EmployeeId).OrderByDescending(s => s.CreatedAtUtc).Take(100).ToListAsync(ct));

    [HttpGet("sales/{id:guid}")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> SaleDetails(Guid id, CancellationToken ct)
    {
        var sale = await data.Query<Sale>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && (CanReadOther || s.EmployeeId == EmployeeId), ct);
        if (sale is null) return NotFound();
        return Ok(new { sale, items = await data.Query<SaleItem>().AsNoTracking().Where(s => s.SaleId == id).ToListAsync(ct),
            refund = await data.Query<SaleRefund>().AsNoTracking().SingleOrDefaultAsync(r => r.SaleId == id, ct),
            payments = await data.Query<Payment>().AsNoTracking().Where(p => p.SaleId == id).OrderBy(p => p.CreatedAtUtc).ToListAsync(ct) });
    }
}
