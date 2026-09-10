namespace GameClub.Domain.Gaming;

public enum SessionOperationType { Extend, Transfer }

/// <summary>Immutable ownership and replay journal for externally retried session mutations.</summary>
public sealed class SessionOperation
{
    private SessionOperation() { }

    public SessionOperation(Guid id, Guid gamingSessionId, SessionOperationType type, string fingerprint,
        DateTime createdAtUtc, Guid? employeeId)
    {
        if (id == Guid.Empty || gamingSessionId == Guid.Empty || employeeId == Guid.Empty)
            throw new ArgumentException("Operation and session identifiers are required.");
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (fingerprint is not { Length: 64 } || fingerprint.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A SHA-256 fingerprint is required.", nameof(fingerprint));
        if (createdAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC timestamp required.", nameof(createdAtUtc));
        Id = id;
        GamingSessionId = gamingSessionId;
        Type = type;
        Fingerprint = fingerprint;
        CreatedAtUtc = createdAtUtc;
        EmployeeId = employeeId;
    }

    public Guid Id { get; private set; }
    public Guid GamingSessionId { get; private set; }
    public SessionOperationType Type { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public Guid? EmployeeId { get; private set; }
}
