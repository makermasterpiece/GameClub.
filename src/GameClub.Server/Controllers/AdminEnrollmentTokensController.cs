using GameClub.Server.Contracts.Security;
using GameClub.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using GameClub.Server.Security.Employees;
using GameClub.Infrastructure.Persistence;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/admin/enrollment-tokens")]
public sealed class AdminEnrollmentTokensController(
    IStationEnrollmentService enrollmentService,
    GameClubDbContext dbContext,
    ISecurityAuditService audit) : ControllerBase
{
    [HttpPost]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Security)]
    [EnableRateLimiting("enrollment")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<CreateEnrollmentTokenResponse>> Create(
        CreateEnrollmentTokenRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await enrollmentService.CreateTokenAsync(
            request.Description,
            cancellationToken);
        await audit.WriteAsync("EmployeeEnrollmentTokenCreated", null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"EmployeeId={Guid.Parse(User.FindFirst("employee_id")!.Value):D};ExpiresAtUtc={result.ExpiresAtUtc:O}",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new CreateEnrollmentTokenResponse(result.Token, result.ExpiresAtUtc));
    }
}
