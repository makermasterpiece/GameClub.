using System.ComponentModel.DataAnnotations;
using GameClub.Domain.Employees;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

public sealed record EmployeeLoginRequest(
    [param: Required, StringLength(Employee.MaximumUsernameLength, MinimumLength = Employee.MinimumUsernameLength)] string Username,
    [param: Required, StringLength(EmployeeManagementService.MaximumPasswordLength)] string Password);

[ApiController]
[Route("api/admin/auth")]
[RequestSizeLimit(4096)]
public sealed class EmployeeAuthController(EmployeeAuthenticationService authentication, EmployeeTokenService tokens,
    GameClubDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(EmployeeLoginRequest request, CancellationToken ct)
    {
        var result = await authentication.LoginAsync(request.Username, request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
        if (result.Response is not null) return Ok(result.Response);
        return result.ErrorCode == "RATE_LIMITED"
            ? StatusCode(StatusCodes.Status429TooManyRequests, new { code = "RATE_LIMITED" })
            : Unauthorized(new { code = "INVALID_CREDENTIALS" });
    }

    [Authorize(Policy = EmployeePermissions.Read)]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var id = Guid.Parse(User.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim)!.Value);
        var employee = await db.Set<Employee>().AsNoTracking().SingleAsync(e => e.Id == id, ct);
        return Ok(tokens.Identity(employee));
    }

    [Authorize(Policy = EmployeePermissions.Read)]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var id = Guid.Parse(User.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim)!.Value);
        await authentication.LogoutAsync(id, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return NoContent();
    }
}
