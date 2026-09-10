using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace GameClub.Server.Security;

public sealed class StationTokenService : IStationTokenService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(1);
    private readonly ITimeLimitedDataProtector _protector;

    public StationTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector("GameClub.StationToken.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Create(Guid stationId) =>
        _protector.Protect(stationId.ToString("D"), TokenLifetime);

    public bool IsValid(string? token, Guid stationId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(token);
            return Guid.TryParse(payload, out var tokenStationId) && tokenStationId == stationId;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
