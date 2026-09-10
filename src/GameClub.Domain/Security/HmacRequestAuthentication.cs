using System.Security.Cryptography;
using System.Text;

namespace GameClub.Domain.Security;

public static class HmacRequestAuthentication
{
    public const string StationIdHeader = "X-GameClub-Station-Id";
    public const string TimestampHeader = "X-GameClub-Timestamp";
    public const string NonceHeader = "X-GameClub-Nonce";
    public const string SignatureHeader = "X-GameClub-Signature";

    public static string CreateCanonical(
        string method,
        string path,
        string timestamp,
        string nonce,
        ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        return string.Join('\n',
            method.ToUpperInvariant(),
            path,
            timestamp,
            nonce,
            SecurityEncoding.Sha256Hex(body));
    }

    public static string Sign(
        ReadOnlySpan<byte> secret,
        string method,
        string path,
        string timestamp,
        string nonce,
        ReadOnlySpan<byte> body)
    {
        var canonical = CreateCanonical(method, path, timestamp, nonce, body);
        var signature = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
        return SecurityEncoding.ToBase64Url(signature);
    }

    public static bool Verify(
        ReadOnlySpan<byte> secret,
        string method,
        string path,
        string timestamp,
        string nonce,
        ReadOnlySpan<byte> body,
        string signature)
    {
        byte[] supplied;
        try
        {
            supplied = SecurityEncoding.FromBase64Url(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = SecurityEncoding.FromBase64Url(
            Sign(secret, method, path, timestamp, nonce, body));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
