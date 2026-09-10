using Microsoft.AspNetCore.DataProtection;

namespace GameClub.Server.Security;

public interface IStationSecretProtector
{
    string Protect(byte[] secret);
    byte[] Unprotect(string protectedSecret);
}

public sealed class StationSecretProtector(IDataProtectionProvider provider) : IStationSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector(
        "GameClub.StationCredential.HmacSecret.v1");

    public string Protect(byte[] secret) => _protector.Protect(Convert.ToBase64String(secret));

    public byte[] Unprotect(string protectedSecret) =>
        Convert.FromBase64String(_protector.Unprotect(protectedSecret));
}
