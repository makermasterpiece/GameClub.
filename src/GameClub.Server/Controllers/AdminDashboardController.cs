using GameClub.Server.Security.Employees;
using GameClub.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/admin/dashboard")]
[Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
public sealed class AdminDashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public Task<DashboardResponse> Get(CancellationToken ct) => dashboard.GetAsync(ct);
}
