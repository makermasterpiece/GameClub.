using GameClub.Agent.Configuration;

namespace GameClub.Agent.Security;

public static class ServerEndpointPolicy
{
    public static Uri Validate(
        ServerOptions server,
        SecurityOptions security,
        IHostEnvironment environment)
    {
        if (!Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Server:BaseUrl must be a valid absolute URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && environment.IsDevelopment() &&
            security.AllowInsecureDevelopmentHttp)
        {
            return uri;
        }

        throw new InvalidOperationException(
            "GameClub Agent requires HTTPS. HTTP is allowed only in Development when " +
            "Security:AllowInsecureDevelopmentHttp is true.");
    }
}
