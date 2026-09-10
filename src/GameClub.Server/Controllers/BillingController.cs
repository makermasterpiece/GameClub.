using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Application.Stations;
using GameClub.Domain.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using GameClub.Server.Security.Employees;

namespace GameClub.Server.Controllers;

public sealed record GroupRequest(string Name);
public sealed record TariffRequest(string Name, Guid StationGroupId, decimal HourlyPrice);
public sealed record PackageRequest(string Name, Guid StationGroupId, int DurationMinutes, decimal Price, TimeOnly? AvailableFrom, TimeOnly? AvailableUntil);
public sealed record PriceRequest(decimal Price);
public sealed record ActiveRequest(bool IsActive);
public sealed record StationGroupRequest(Guid StationGroupId);
public sealed record WalletChangeRequest(decimal Amount, Guid OperationId);

[ApiController]
[Route("api/admin/catalog")]
[RequestSizeLimit(4096)]
public sealed class CatalogController(CatalogService service, StationManagementService stations, IClubData data) : ControllerBase
{
    private Guid EmployeeId => Guid.Parse(User.FindFirst("employee_id")!.Value);

    [HttpGet]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(new
    {
        groups = await data.Query<StationGroup>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
        tariffs = await data.Query<Tariff>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
        packages = await data.Query<TariffPackage>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)
    });

    [HttpPost("groups")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> Group(GroupRequest request, CancellationToken ct) =>
        Ok(await service.CreateGroupAsync(request.Name, EmployeeId, ct));

    [HttpPost("tariffs")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> Tariff(TariffRequest request, CancellationToken ct) =>
        Ok(await service.CreateTariffAsync(request.Name, request.StationGroupId, request.HourlyPrice, EmployeeId, ct));

    [HttpPost("packages")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> Package(PackageRequest request, CancellationToken ct) =>
        Ok(await service.CreatePackageAsync(request.Name, request.StationGroupId, request.DurationMinutes, request.Price, request.AvailableFrom, request.AvailableUntil, EmployeeId, ct));

    [HttpPut("tariffs/{id:guid}/price")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> Price(Guid id, PriceRequest request, CancellationToken ct) =>
        Ok(await service.UpdateTariffPriceAsync(id, request.Price, EmployeeId, ct));

    [HttpPut("tariffs/{id:guid}/active")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> TariffActive(Guid id, ActiveRequest request, CancellationToken ct) =>
        Ok(await service.SetTariffActiveAsync(id, request.IsActive, EmployeeId, ct));

    [HttpPut("packages/{id:guid}/active")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> PackageActive(Guid id, ActiveRequest request, CancellationToken ct) =>
        Ok(await service.SetPackageActiveAsync(id, request.IsActive, EmployeeId, ct));

    [HttpPut("stations/{id:guid}/group")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> AssignGroup(Guid id, StationGroupRequest request, CancellationToken ct) =>
        Ok(await stations.AssignGroupAsync(id, request.StationGroupId, EmployeeId, ct));
}

[ApiController]
[Route("api/admin/users/{userId:guid}/wallet")]
[RequestSizeLimit(2048)]
public sealed class WalletController(WalletService wallets, IClubData data) : ControllerBase
{
    private Guid EmployeeId => Guid.Parse(User.FindFirst("employee_id")!.Value);

    [HttpGet]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct)
    {
        var result = await data.AtomicAsync(async token =>
        {
            var wallet = await data.Query<Wallet>().SingleOrDefaultAsync(x => x.UserId == userId, token);
            var held = wallet is null ? 0m : await data.Query<WalletReservation>()
                .Where(x => x.WalletId == wallet.Id && x.ReleasedAtUtc == null).SumAsync(x => x.Amount, token);
            return new { userId, balance = wallet?.Balance ?? 0m, reserved = held, available = (wallet?.Balance ?? 0m) - held };
        }, ct);
        return Ok(result);
    }

    [HttpGet("transactions")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Transactions(Guid userId, CancellationToken ct) =>
        Ok(await data.Query<WalletTransaction>().AsNoTracking()
            .Where(x => data.Query<Wallet>().Any(w => w.Id == x.WalletId && w.UserId == userId))
            .OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct));

    [HttpPost("deposit")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Deposit(Guid userId, WalletChangeRequest request, CancellationToken ct) =>
        Ok(await wallets.DepositAsync(userId, request.Amount, request.OperationId, EmployeeId, ct));

    [HttpPost("adjustment")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Money)]
    public async Task<IActionResult> Adjustment(Guid userId, WalletChangeRequest request, CancellationToken ct) =>
        Ok(await wallets.AdjustmentAsync(userId, request.Amount, request.OperationId, EmployeeId, ct));
}
