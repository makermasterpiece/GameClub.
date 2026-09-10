using GameClub.Domain.Security;
using GameClub.Infrastructure.Persistence;

namespace GameClub.Server.Security;

public interface ISecurityAuditService
{
    Task WriteAsync(
        string eventType,
        Guid? stationId,
        string? sourceIp,
        string? details,
        CancellationToken cancellationToken,
        Guid? userId = null);
}

public sealed class SecurityAuditService(
    GameClubDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<SecurityAuditService> logger) : ISecurityAuditService
{
    public async Task WriteAsync(
        string eventType,
        Guid? stationId,
        string? sourceIp,
        string? details,
        CancellationToken cancellationToken,
        Guid? userId = null)
    {
        var safeDetails = string.IsNullOrWhiteSpace(details)
            ? null
            : details.Trim()[..Math.Min(details.Trim().Length, 1000)];
        dbContext.SecurityAuditEvents.Add(new SecurityAuditEvent(
            Guid.NewGuid(),
            eventType,
            stationId,
            timeProvider.GetUtcNow().UtcDateTime,
            sourceIp,
            safeDetails,
            userId));
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Security audit {SecurityEventType} for station {StationId} and user {UserId}: {Details}",
            eventType,
            stationId,
            userId,
            safeDetails);
    }
}
