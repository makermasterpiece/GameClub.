using System.Globalization;
using GameClub.Domain.Security;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Security;

public enum StationAuthenticationFailure
{
    None,
    MissingHeaders,
    InvalidStationId,
    InvalidTimestamp,
    TimestampOutsideWindow,
    CredentialNotFound,
    InvalidNonce,
    InvalidSignature,
    ReplayDetected
}

public sealed record StationAuthenticationResult(
    bool Succeeded,
    Guid? StationId,
    StationAuthenticationFailure Failure)
{
    public static StationAuthenticationResult Success(Guid stationId) =>
        new(true, stationId, StationAuthenticationFailure.None);

    public static StationAuthenticationResult Fail(
        StationAuthenticationFailure failure,
        Guid? stationId = null) => new(false, stationId, failure);
}

public interface IStationHmacAuthenticator
{
    Task<StationAuthenticationResult> AuthenticateAsync(
        HttpRequest request,
        string? sourceIp,
        CancellationToken cancellationToken);
}

public sealed class StationHmacAuthenticator(
    GameClubDbContext dbContext,
    IStationSecretProtector secretProtector,
    IReplayProtectionStore replayProtection,
    ISecurityAuditService audit,
    TimeProvider timeProvider,
    ILogger<StationHmacAuthenticator> logger) : IStationHmacAuthenticator
{
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(2);

    public async Task<StationAuthenticationResult> AuthenticateAsync(
        HttpRequest request,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var stationValue = request.Headers[HmacRequestAuthentication.StationIdHeader].ToString();
        var timestampValue = request.Headers[HmacRequestAuthentication.TimestampHeader].ToString();
        var nonce = request.Headers[HmacRequestAuthentication.NonceHeader].ToString();
        var signature = request.Headers[HmacRequestAuthentication.SignatureHeader].ToString();

        if (string.IsNullOrWhiteSpace(stationValue) || string.IsNullOrWhiteSpace(timestampValue) ||
            string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature))
        {
            return await RejectAsync(
                StationAuthenticationFailure.MissingHeaders,
                null,
                sourceIp,
                cancellationToken);
        }

        if (!Guid.TryParse(stationValue, out var stationId))
        {
            return await RejectAsync(
                StationAuthenticationFailure.InvalidStationId,
                null,
                sourceIp,
                cancellationToken);
        }

        if (!long.TryParse(timestampValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return await RejectAsync(
                StationAuthenticationFailure.InvalidTimestamp,
                stationId,
                sourceIp,
                cancellationToken);
        }

        DateTime timestampUtc;
        try
        {
            timestampUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return await RejectAsync(
                StationAuthenticationFailure.InvalidTimestamp,
                stationId,
                sourceIp,
                cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if ((timestampUtc - now).Duration() > AllowedClockSkew)
        {
            return await RejectAsync(
                StationAuthenticationFailure.TimestampOutsideWindow,
                stationId,
                sourceIp,
                cancellationToken);
        }

        byte[] nonceBytes;
        try
        {
            nonceBytes = SecurityEncoding.FromBase64Url(nonce);
        }
        catch (FormatException)
        {
            return await RejectAsync(
                StationAuthenticationFailure.InvalidNonce,
                stationId,
                sourceIp,
                cancellationToken);
        }

        if (nonceBytes.Length is < 16 or > 64)
        {
            return await RejectAsync(
                StationAuthenticationFailure.InvalidNonce,
                stationId,
                sourceIp,
                cancellationToken);
        }

        var credential = await dbContext.StationCredentials
            .Where(candidate => candidate.StationId == stationId && candidate.RevokedAtUtc == null)
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            return await RejectAsync(
                StationAuthenticationFailure.CredentialNotFound,
                stationId,
                sourceIp,
                cancellationToken);
        }

        request.Body.Position = 0;
        using var bodyBuffer = new MemoryStream();
        await request.Body.CopyToAsync(bodyBuffer, cancellationToken);
        request.Body.Position = 0;

        byte[] secret;
        try
        {
            secret = secretProtector.Unprotect(credential.ProtectedSecret);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not unprotect credential for station {StationId}", stationId);
            return await RejectAsync(
                StationAuthenticationFailure.CredentialNotFound,
                stationId,
                sourceIp,
                cancellationToken);
        }

        try
        {
            byte[] storedHash;
            try
            {
                storedHash = Convert.FromHexString(credential.SecretHash);
            }
            catch (FormatException)
            {
                return await RejectAsync(
                    StationAuthenticationFailure.CredentialNotFound,
                    stationId,
                    sourceIp,
                    cancellationToken);
            }

            var actualHash = System.Security.Cryptography.SHA256.HashData(secret);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    storedHash,
                    actualHash))
            {
                return await RejectAsync(
                    StationAuthenticationFailure.CredentialNotFound,
                    stationId,
                    sourceIp,
                    cancellationToken);
            }

            if (!HmacRequestAuthentication.Verify(
                    secret,
                    request.Method,
                    request.Path.Value ?? "/",
                    timestampValue,
                    nonce,
                    bodyBuffer.ToArray(),
                    signature))
            {
                return await RejectAsync(
                    StationAuthenticationFailure.InvalidSignature,
                    stationId,
                    sourceIp,
                    cancellationToken);
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }

        if (!replayProtection.TryUse(stationId, nonce, now + NonceLifetime))
        {
            return await RejectAsync(
                StationAuthenticationFailure.ReplayDetected,
                stationId,
                sourceIp,
                cancellationToken);
        }

        credential.MarkUsed(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return StationAuthenticationResult.Success(stationId);
    }

    private async Task<StationAuthenticationResult> RejectAsync(
        StationAuthenticationFailure failure,
        Guid? stationId,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var eventType = failure switch
        {
            StationAuthenticationFailure.InvalidSignature => "AgentSignatureInvalid",
            StationAuthenticationFailure.ReplayDetected => "ReplayDetected",
            _ => "AgentAuthenticationFailed"
        };
        logger.LogWarning(
            "Agent authentication rejected for station {StationId}. Reason: {Reason}",
            stationId,
            failure);
        await audit.WriteAsync(eventType, stationId, sourceIp, failure.ToString(), cancellationToken);
        return StationAuthenticationResult.Fail(failure, stationId);
    }
}
