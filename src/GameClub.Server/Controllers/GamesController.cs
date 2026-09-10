using GameClub.Application.Abstractions;
using GameClub.Application.Games;
using GameClub.Contracts.Games;
using GameClub.Domain.Games;
using GameClub.Domain.Stations;
using GameClub.Server.Security;
using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

public sealed record SaveGameRequest(string Name, string? PlayniteGameId, string? Executable, string? CoverUrl, bool IsActive);

[ApiController]
[Route("api/admin/games")]
[RequestSizeLimit(8192)]
public sealed class GamesController(GameCatalogService catalog, IClubData data) : ControllerBase
{
    [HttpGet]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await data.Query<Game>().AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct));

    [HttpPost]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> Create(SaveGameRequest request, CancellationToken ct) =>
        Ok(await Save(null, request, ct));

    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Catalog)]
    public async Task<IActionResult> Update(Guid id, SaveGameRequest request, CancellationToken ct) =>
        Ok(await Save(id, request, ct));

    [HttpGet("/api/admin/stations/{stationId:guid}/games")]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    public async Task<IActionResult> StationGames(Guid stationId, CancellationToken ct)
    {
        if (!await data.Query<Station>().AnyAsync(s => s.Id == stationId, ct)) return NotFound();
        return Ok(await (from game in data.Query<Game>().AsNoTracking()
            join report in data.Query<StationGame>().AsNoTracking().Where(s => s.StationId == stationId)
                on game.Id equals report.GameId into reports
            from report in reports.DefaultIfEmpty()
            orderby game.Name
            select new { gameId = game.Id, game.Name, game.PlayniteGameId, game.IsActive,
                installed = report != null && report.Installed,
                lastDetectedAtUtc = report == null ? (DateTime?)null : report.LastDetectedAtUtc }).ToListAsync(ct));
    }

    private Task<Game> Save(Guid? id, SaveGameRequest request, CancellationToken ct) =>
        catalog.SaveAsync(id, request.Name, request.PlayniteGameId, request.Executable, request.CoverUrl,
            request.IsActive, Guid.Parse(User.FindFirst("employee_id")!.Value), ct);
}

[ApiController]
[Route("api/agent/games")]
[EnableRateLimiting("agent-session")]
public sealed class AgentGamesController(IStationHmacAuthenticator authenticator, GameCatalogService catalog,
    GameLaunchService launch) : ControllerBase
{
    [HttpPost("inventory")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<IActionResult> Inventory(StationGameInventoryRequest request, CancellationToken ct)
    {
        var auth = await authenticator.AuthenticateAsync(Request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (!auth.Succeeded || auth.StationId is not Guid stationId) return Unauthorized();
        await catalog.ReportInventoryAsync(stationId, request.Games, ct);
        return NoContent();
    }

    [HttpPost("authorize")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> AuthorizeGame(GameAuthorizationRequest request, CancellationToken ct)
    {
        var auth = await authenticator.AuthenticateAsync(Request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (!auth.Succeeded || auth.StationId is not Guid stationId) return Unauthorized();
        return Ok(await launch.AuthorizeAsync(stationId, request.GamingSessionId, request.GameId, ct));
    }
}
