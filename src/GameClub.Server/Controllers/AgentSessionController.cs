using GameClub.Server.Contracts.Security;
using GameClub.Server.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/agent")]
public sealed class AgentSessionController(
    IStationHmacAuthenticator authenticator,
    IAgentSessionTokenService tokenService) : ControllerBase
{
    [HttpPost("session-token")]
    [EnableRateLimiting("agent-session")]
    [RequestSizeLimit(1024)]
    public async Task<ActionResult<AgentSessionTokenResponse>> CreateSessionToken(
        CancellationToken cancellationToken)
    {
        var authentication = await authenticator.AuthenticateAsync(
            Request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!authentication.Succeeded || authentication.StationId is not Guid stationId)
        {
            return Unauthorized();
        }

        var token = tokenService.Create(stationId);
        return Ok(new AgentSessionTokenResponse(token.AccessToken, token.ExpiresAtUtc));
    }
}
