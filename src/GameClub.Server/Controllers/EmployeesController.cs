using System.ComponentModel.DataAnnotations;
using GameClub.Domain.Employees;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

public sealed record CreateEmployeeRequest(
    [param: Required, StringLength(Employee.MaximumUsernameLength, MinimumLength = Employee.MinimumUsernameLength)] string Username,
    [param: Required, StringLength(EmployeeManagementService.MaximumPasswordLength, MinimumLength = EmployeeManagementService.MinimumPasswordLength)] string Password,
    [param: Required] EmployeeRole? Role);
public sealed record ChangeEmployeeAccessRequest([param: Required] EmployeeRole? Role, [param: Required] bool? IsActive);
public sealed record ChangeEmployeePasswordRequest(
    [param: Required, StringLength(EmployeeManagementService.MaximumPasswordLength, MinimumLength = EmployeeManagementService.MinimumPasswordLength)] string Password);

[ApiController]
[Route("api/admin/employees")]
[Authorize(Policy = EmployeePermissions.Employees)]
[RequestSizeLimit(4096)]
public sealed class EmployeesController(EmployeeManagementService management, GameClubDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await db.Set<Employee>().AsNoTracking()
        .OrderBy(e => e.Username).Select(e => new EmployeeAdminResponse(e.Id, e.Username, e.Role.ToString(),
            e.IsActive, e.CreatedAtUtc, e.LastLoginAtUtc)).ToListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateEmployeeRequest request, CancellationToken ct)
    {
        var employee = await management.CreateAsync(ActorId, request.Username, request.Password, request.Role!.Value, ct);
        return Created($"/api/admin/employees/{employee.Id:D}", employee);
    }

    [HttpPut("{id:guid}/access")]
    public async Task<IActionResult> ChangeAccess(Guid id, ChangeEmployeeAccessRequest request, CancellationToken ct) =>
        Ok(await management.ChangeAccessAsync(ActorId, id, request.Role!.Value, request.IsActive!.Value, ct));

    [HttpPut("{id:guid}/password")]
    public async Task<IActionResult> ChangePassword(Guid id, ChangeEmployeePasswordRequest request, CancellationToken ct)
    {
        await management.ChangePasswordAsync(ActorId, id, request.Password, ct);
        return NoContent();
    }

    private Guid ActorId => Guid.Parse(User.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim)!.Value);
}
