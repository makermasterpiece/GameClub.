namespace GameClub.Domain.Security;

public sealed class SecurityAuditEvent
{
    private SecurityAuditEvent()
    {
    }

    public SecurityAuditEvent(
        Guid id,
        string eventType,
        Guid? stationId,
        DateTime timestampUtc,
        string? sourceIp,
        string? details,
        Guid? userId = null)
    {
        Id = id;
        EventType = eventType;
        StationId = stationId;
        TimestampUtc = timestampUtc;
        SourceIp = sourceIp;
        Details = details;
        UserId = userId;
    }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public Guid? StationId { get; private set; }
    public DateTime TimestampUtc { get; private set; }
    public string? SourceIp { get; private set; }
    public string? Details { get; private set; }
    public Guid? UserId { get; private set; }
}
