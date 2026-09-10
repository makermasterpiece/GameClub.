namespace GameClub.Domain.Gaming;

public sealed class SessionEvent
{
    public const int MaximumDetailsLength = 2000;

    private SessionEvent()
    {
    }

    public SessionEvent(
        Guid id,
        Guid gamingSessionId,
        SessionEventType type,
        DateTime createdAtUtc,
        string? details = null,
        Guid? employeeId = null)
    {
        if (id == Guid.Empty || gamingSessionId == Guid.Empty || employeeId == Guid.Empty)
        {
            throw new ArgumentException("Event, session and optional employee identifiers must be non-empty.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Event time must be UTC.", nameof(createdAtUtc));
        }

        if (details?.Length > MaximumDetailsLength)
        {
            throw new ArgumentOutOfRangeException(nameof(details));
        }

        Id = id;
        GamingSessionId = gamingSessionId;
        Type = type;
        CreatedAtUtc = createdAtUtc;
        Details = details;
        EmployeeId = employeeId;
    }

    public Guid Id { get; private set; }
    public Guid GamingSessionId { get; private set; }
    public SessionEventType Type { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? Details { get; private set; }
    public Guid? EmployeeId { get; private set; }
}
