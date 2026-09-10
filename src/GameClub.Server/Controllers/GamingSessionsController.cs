using Microsoft.AspNetCore.Authorization;
using GameClub.Server.Security.Employees;
using GameClub.Application.Abstractions;
using GameClub.Application.Gaming;
using GameClub.Application.Billing;
using GameClub.Domain.Gaming;
using GameClub.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using GameClub.Domain.Stations;

namespace GameClub.Server.Controllers;

public sealed record ExtendGamingSessionRequest([Required] Guid? OperationId, [Range(1, 10080)] int Minutes);
public sealed record TransferGamingSessionRequest([Required] Guid? OperationId, [Required] Guid? DestinationStationId);

[ApiController]
[Route("api/admin/gaming-sessions")]
[RequestSizeLimit(4096)]
public sealed class GamingSessionsController(GamingSessionService service, IClubData data) : ControllerBase
{
    private Guid EmployeeId => Guid.Parse(User.FindFirst("employee_id")!.Value);

    [HttpPost]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Start(SessionPurchase request, CancellationToken ct) =>
        Ok(await service.StartPaidAsync(request, EmployeeId, ct));

    [HttpPost("{id:guid}/pause")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct) =>
        Ok(await service.PauseAsync(id, EmployeeId, ct));

    [HttpPost("{id:guid}/resume")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct) =>
        Ok(await service.ResumeAsync(id, EmployeeId, ct));

    [HttpPost("{id:guid}/end")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> End(Guid id, CancellationToken ct) =>
        Ok(await service.EndAsync(id, EmployeeId, ct));

    [HttpGet("{id:guid}/extension-quote")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> ExtensionQuote(Guid id, [FromQuery, Range(1, 10080)] int minutes, CancellationToken ct) =>
        Ok(await service.QuoteExtensionAsync(id, minutes, ct));

    [HttpPost("{id:guid}/extend")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Extend(Guid id, ExtendGamingSessionRequest request, CancellationToken ct) =>
        Ok(await service.ExtendAsync(id, request.OperationId!.Value, request.Minutes, EmployeeId, ct));

    [HttpPost("{id:guid}/transfer")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Operate)]
    public async Task<IActionResult> Transfer(Guid id, TransferGamingSessionRequest request, CancellationToken ct) =>
        Ok(await service.TransferAsync(id, request.OperationId!.Value, request.DestinationStationId!.Value, EmployeeId, ct));

    [HttpGet("{id:guid}/segments")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Segments(Guid id, CancellationToken ct) =>
        Ok(await (from segment in data.Query<StationSessionSegment>().AsNoTracking()
            join station in data.Query<Station>() on segment.StationId equals station.Id
            where segment.GamingSessionId == id
            orderby segment.StartedAtUtc, segment.Id
            select new
            {
                segment.Id, segment.GamingSessionId, segment.StationId,
                StationName = station.Name, segment.StartedAtUtc, segment.EndedAtUtc
            }).ToListAsync(ct));

    [HttpGet("{id:guid}/events")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Events(Guid id, CancellationToken ct) =>
        Ok(await data.Query<SessionEvent>().AsNoTracking().Where(e => e.GamingSessionId == id).OrderBy(e => e.CreatedAtUtc).ToListAsync(ct));
}

[ApiController]
[Route("api/agent/gaming/session/current")]
public sealed class AgentGamingSessionController(GamingSessionService service, IStationHmacAuthenticator authenticator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var result = await authenticator.AuthenticateAsync(Request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return result.Succeeded && result.StationId is Guid stationId
            ? Ok(await service.CurrentAsync(stationId, ct)) : Unauthorized();
    }
}
