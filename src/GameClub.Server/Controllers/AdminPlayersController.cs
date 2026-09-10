using System.ComponentModel.DataAnnotations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Users;
using GameClub.Server.Security;
using GameClub.Server.Security.Employees;
using GameClub.Server.Services.Players;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Player = GameClub.Domain.Users.User;

namespace GameClub.Server.Controllers;

public sealed record AdminPlayerResponse(Guid Id, string Username, string? DisplayName,
    string? Email, string? Phone, UserStatus Status, DateTime CreatedAtUtc, DateTime? LastLoginAtUtc);

public sealed record PlayerStatusRequest([param: Required, EnumDataType(typeof(UserStatus))] UserStatus? Status);

[ApiController]
[Route("api/admin/users")]
[RequestSizeLimit(4096)]
public sealed class AdminPlayersController(GameClubDbContext db, IUserProvisioningService users,
    ISecurityAuditService audit) : ControllerBase
{
    private Guid EmployeeId => Guid.Parse(User.FindFirst("employee_id")!.Value);

    [HttpGet]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Get([FromQuery] string? search, CancellationToken ct)
    {
        var term = search?.Trim();
        if (term?.Length > Player.MaximumDisplayNameLength)
            return BadRequest(new { code = "SEARCH_TOO_LONG" });
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrEmpty(term))
        {
            var normalized = Player.NormalizeUsername(term);
            query = query.Where(user => user.NormalizedUsername.Contains(normalized) ||
                user.DisplayName != null && user.DisplayName.ToUpper().Contains(normalized));
        }

        return Ok(await query.OrderBy(user => user.Username).ThenBy(user => user.Id).Take(100)
            .Select(user => new AdminPlayerResponse(user.Id, user.Username, user.DisplayName,
                user.Email, user.Phone, user.Status, user.CreatedAtUtc, user.LastLoginAtUtc)).ToListAsync(ct));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Create(CreateDevelopmentUserRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await users.CreateAsync(request.Username, request.Password, request.DisplayName,
            request.Email, request.Phone, ct);
        if (!result.Succeeded || result.User is null)
        {
            return result.Error == CreateUserError.DuplicateUsername
                ? Conflict(new { code = "USERNAME_ALREADY_EXISTS" })
                : BadRequest(new { code = "INVALID_USER_DETAILS" });
        }

        var user = result.User;
        await audit.WriteAsync("PlayerAccountCreated", null,
            HttpContext.Connection.RemoteIpAddress?.ToString(), $"EmployeeId={EmployeeId:D}", ct, user.Id);
        await transaction.CommitAsync(ct);
        return StatusCode(StatusCodes.Status201Created, ToResponse(user));
    }

    [HttpPut("{id:guid}/status")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Money)]
    public async Task<IActionResult> ChangeStatus(Guid id, PlayerStatusRequest request, CancellationToken ct)
    {
        if (!request.Status.HasValue || !Enum.IsDefined(request.Status.Value))
            return BadRequest(new { code = "INVALID_USER_STATUS" });
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();
        var previous = user.Status;
        user.ChangeStatus(request.Status.Value);
        await audit.WriteAsync("PlayerAccountStatusChanged", null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"EmployeeId={EmployeeId:D};PreviousStatus={previous};Status={user.Status}", ct, user.Id);
        return Ok(ToResponse(user));
    }

    private static AdminPlayerResponse ToResponse(Player user) => new(user.Id, user.Username,
        user.DisplayName, user.Email, user.Phone, user.Status, user.CreatedAtUtc, user.LastLoginAtUtc);
}
