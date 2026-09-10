namespace GameClub.Agent.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string BaseUrl { get; init; } = string.Empty;
}
