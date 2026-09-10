using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GameClub.Server.Security;

public sealed record AgentSessionToken(string AccessToken, DateTime ExpiresAtUtc, string Jti);

public interface IAgentSessionTokenService
{
    AgentSessionToken Create(Guid stationId);
}

public sealed class AgentSessionTokenService(
    IServerSigningKeyProvider signingKeys,
    IOptions<SecurityOptions> options,
    TimeProvider timeProvider) : IAgentSessionTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public AgentSessionToken Create(Guid stationId)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now + Lifetime;
        var jti = Guid.NewGuid().ToString("D");
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, stationId.ToString("D")),
            new Claim("station_id", stationId.ToString("D")),
            new Claim("agent", "true"),
            new Claim(JwtRegisteredClaimNames.Jti, jti)
        };
        var token = new JwtSecurityToken(
            options.Value.JwtIssuer,
            "gameclub-agent",
            claims,
            now.AddSeconds(-5),
            expires,
            signingKeys.JwtSigningCredentials);
        return new AgentSessionToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            jti);
    }
}
