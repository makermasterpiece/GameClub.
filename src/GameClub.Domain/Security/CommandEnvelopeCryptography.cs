using System.Security.Cryptography;
using System.Text;
using GameClub.Domain.Commands;

namespace GameClub.Domain.Security;

public static class CommandEnvelopeCryptography
{
    public const string Version = "1";
    public const string SignatureAlgorithm = "ECDSA_P256_SHA256";
    public const int MaximumPayloadBytes = 16 * 1024;
    public const int MaximumTestMessageLength = 1000;

    public static string CreateCanonical(
        Guid commandId,
        Guid stationId,
        string type,
        string? payloadJson,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        string nonce) =>
        string.Join('\n',
            Version,
            commandId.ToString("D"),
            stationId.ToString("D"),
            type,
            SecurityEncoding.Sha256Hex(Encoding.UTF8.GetBytes(payloadJson ?? string.Empty)),
            new DateTimeOffset(createdAtUtc.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            new DateTimeOffset(expiresAtUtc.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            nonce);

    public static string Sign(ECDsa privateKey, SignedAgentCommandEnvelope envelope)
    {
        var canonical = CreateCanonical(
            envelope.CommandId,
            envelope.StationId,
            envelope.Type,
            envelope.PayloadJson,
            envelope.CreatedAtUtc,
            envelope.ExpiresAtUtc,
            envelope.Nonce);
        return SecurityEncoding.ToBase64Url(
            privateKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
    }

    public static bool Verify(ECDsa publicKey, SignedAgentCommandEnvelope envelope)
    {
        if (!string.Equals(
                envelope.SignatureAlgorithm,
                SignatureAlgorithm,
                StringComparison.Ordinal))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = SecurityEncoding.FromBase64Url(envelope.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var canonical = CreateCanonical(
            envelope.CommandId,
            envelope.StationId,
            envelope.Type,
            envelope.PayloadJson,
            envelope.CreatedAtUtc,
            envelope.ExpiresAtUtc,
            envelope.Nonce);
        return publicKey.VerifyData(
            Encoding.UTF8.GetBytes(canonical),
            signature,
            HashAlgorithmName.SHA256);
    }
}
