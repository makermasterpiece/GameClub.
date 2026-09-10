using System.Security.Cryptography;
using System.Text;
using GameClub.Domain.Security;
using GameClub.Domain.Stations;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Security;

public sealed record CreatedEnrollmentToken(string Token, DateTime ExpiresAtUtc);

public sealed record EnrolledStationIdentity(
    Guid StationId,
    string StationSecret,
    string ServerPublicKey,
    string SignatureAlgorithm);

public sealed record StationEnrollmentResult(
    bool Succeeded,
    EnrolledStationIdentity? Identity,
    string? Error);

public interface IStationEnrollmentService
{
    Task<CreatedEnrollmentToken> CreateTokenAsync(
        string? description,
        CancellationToken cancellationToken);

    Task<StationEnrollmentResult> EnrollAsync(
        string enrollmentToken,
        string stationName,
        string machineName,
        string agentVersion,
        string? sourceIp,
        CancellationToken cancellationToken);
}

public sealed class StationEnrollmentService(
    GameClubDbContext dbContext,
    IStationSecretProtector secretProtector,
    IServerCommandSigner commandSigner,
    ISecurityAuditService audit,
    TimeProvider timeProvider) : IStationEnrollmentService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public async Task<CreatedEnrollmentToken> CreateTokenAsync(
        string? description,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rawToken = SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        dbContext.StationEnrollmentTokens.Add(new StationEnrollmentToken(
            Guid.NewGuid(),
            Hash(rawToken),
            now,
            now + TokenLifetime,
            description));
        await dbContext.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "StationEnrollmentCreated",
            null,
            null,
            string.IsNullOrWhiteSpace(description) ? null : "Description provided",
            cancellationToken);
        return new CreatedEnrollmentToken(rawToken, now + TokenLifetime);
    }

    public async Task<StationEnrollmentResult> EnrollAsync(
        string enrollmentToken,
        string stationName,
        string machineName,
        string agentVersion,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var tokenHash = Hash(enrollmentToken);
        var token = await dbContext.StationEnrollmentTokens
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);

        if (token is null || !token.TryUse(now))
        {
            await audit.WriteAsync(
                "StationEnrollmentRejected",
                null,
                sourceIp,
                token is null ? "Unknown token" : "Token used, expired, or revoked",
                cancellationToken);
            return new StationEnrollmentResult(false, null, "Enrollment token is invalid.");
        }

        var normalizedMachineName = machineName.Trim();
        var station = await dbContext.Stations.SingleOrDefaultAsync(
            candidate => candidate.MachineName == normalizedMachineName,
            cancellationToken);
        if (station is null)
        {
            station = new Station(
                Guid.NewGuid(),
                stationName,
                normalizedMachineName,
                sourceIp,
                agentVersion,
                now);
            dbContext.Stations.Add(station);
        }
        else
        {
            station.UpdateFromAgent(stationName, normalizedMachineName, sourceIp, agentVersion, now);
            var currentCredentials = await dbContext.StationCredentials
                .Where(credential => credential.StationId == station.Id && credential.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var currentCredential in currentCredentials)
            {
                currentCredential.Revoke(now);
            }
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        var rawSecret = SecurityEncoding.ToBase64Url(secret);
        var credential = new StationCredential(
            Guid.NewGuid(),
            station.Id,
            SecurityEncoding.Sha256Hex(secret),
            secretProtector.Protect(secret),
            now);
        dbContext.StationCredentials.Add(credential);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            CryptographicOperations.ZeroMemory(secret);
            dbContext.ChangeTracker.Clear();
            await audit.WriteAsync(
                "StationEnrollmentRejected",
                null,
                sourceIp,
                "Enrollment token was consumed concurrently",
                cancellationToken);
            return new StationEnrollmentResult(false, null, "Enrollment token is invalid.");
        }

        CryptographicOperations.ZeroMemory(secret);
        await audit.WriteAsync(
            "StationEnrolled",
            station.Id,
            sourceIp,
            "Station credential created",
            cancellationToken);
        return new StationEnrollmentResult(
            true,
            new EnrolledStationIdentity(
                station.Id,
                rawSecret,
                commandSigner.PublicKeyBase64,
                CommandEnvelopeCryptography.SignatureAlgorithm),
            null);
    }

    private static string Hash(string value) =>
        SecurityEncoding.Sha256Hex(Encoding.UTF8.GetBytes(value));
}
