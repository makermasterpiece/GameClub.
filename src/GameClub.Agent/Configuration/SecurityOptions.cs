namespace GameClub.Agent.Configuration;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool AllowInsecureDevelopmentHttp { get; set; }
    public string? EnrollmentToken { get; set; }
    public string? CredentialFilePath { get; set; }
    public string? ProcessedCommandStorePath { get; set; }
}
