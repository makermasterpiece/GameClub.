namespace GameClub.Server.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool EnableDevelopmentAdminEndpoints { get; set; }
    public bool EnableLegacyStationRegistration { get; set; }
    public string? CommandSigningPrivateKeyPath { get; set; }
    public string? CommandSigningCertificatePath { get; set; }
    public string? CommandSigningCertificatePassword { get; set; }
    public string? DataProtectionKeysPath { get; set; }
    public string JwtIssuer { get; set; } = "gameclub-server";
}
