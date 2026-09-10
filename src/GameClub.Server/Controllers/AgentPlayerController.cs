using GameClub.Server.Contracts.Players;
using GameClub.Application.Gaming;
using GameClub.Server.Security;
using GameClub.Server.Services.Players;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/agent/player")]
public sealed class AgentPlayerController(
    IStationHmacAuthenticator authenticator,
    IPlayerAuthenticationService playerAuthentication,
    GamingSessionService gamingSessions) : ControllerBase
{
    [HttpPost("login")]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType<PlayerSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<PlayerErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<PlayerErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<PlayerErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<PlayerErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PlayerSessionResponse>> Login(
        PlayerLoginRequest request,
        CancellationToken cancellationToken)
    {
        var stationId = await AuthenticateAsync(cancellationToken);
        if (stationId is null)
        {
            return Unauthorized();
        }

        var result = await playerAuthentication.LoginAsync(
            stationId.Value,
            request.Username,
            request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (result.Succeeded && result.Session is not null)
        {
            return Ok(ToResponse(result.Session));
        }

        var error = new PlayerErrorResponse(
            false,
            PlayerAuthenticationService.ToCode(result.Error));
        return result.Error switch
        {
            PlayerLoginError.InvalidCredentials => Unauthorized(error),
            PlayerLoginError.AccountDisabled or PlayerLoginError.AccountBanned =>
                StatusCode(StatusCodes.Status403Forbidden, error),
            PlayerLoginError.RateLimited =>
                StatusCode(StatusCodes.Status429TooManyRequests, error),
            _ => Conflict(error)
        };
    }

    [HttpPost("logout")]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<PlayerLogoutResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlayerLogoutResponse>> Logout(PlayerLogoutRequest request, CancellationToken cancellationToken)
    {
        var stationId = await AuthenticateAsync(cancellationToken);
        if (stationId is null)
        {
            return Unauthorized();
        }

        if (request.ExpectedSessionId is null || request.ExpectedSessionId == Guid.Empty)
            return BadRequest(new PlayerErrorResponse(false, "EXPECTED_SESSION_REQUIRED"));

        await gamingSessions.LogoutStationAsync(
            stationId.Value,
            request.ExpectedSessionId.Value,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return Ok(new PlayerLogoutResponse(true));
    }

    [HttpGet("session/current")]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<PlayerSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlayerSessionResponse>> Current(CancellationToken cancellationToken)
    {
        var stationId = await AuthenticateAsync(cancellationToken);
        if (stationId is null)
        {
            return Unauthorized();
        }

        var session = await playerAuthentication.GetCurrentAsync(
            stationId.Value,
            cancellationToken);
        return session is null ? NoContent() : Ok(ToResponse(session));
    }

    private async Task<Guid?> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var authentication = await authenticator.AuthenticateAsync(
            Request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return authentication.Succeeded ? authentication.StationId : null;
    }

    private static PlayerSessionResponse ToResponse(PlayerSessionData session) =>
        new(
            true,
            session.SessionId,
            session.CreatedAtUtc,
            session.ExpiresAtUtc,
            new PlayerUserResponse(
                session.UserId,
                session.Username,
                session.DisplayName));
}
