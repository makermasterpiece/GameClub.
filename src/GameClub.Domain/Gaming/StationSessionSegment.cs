namespace GameClub.Domain.Gaming;

/// <summary>Physical station occupancy interval; pauses do not split this history.</summary>
public sealed class StationSessionSegment
{
    private StationSessionSegment() { }

    public StationSessionSegment(Guid id, Guid gamingSessionId, Guid stationId, DateTime startedAtUtc)
    {
        if (id == Guid.Empty || gamingSessionId == Guid.Empty || stationId == Guid.Empty)
            throw new ArgumentException("Segment, session and station identifiers are required.");
        RequireUtc(startedAtUtc);
        Id = id;
        GamingSessionId = gamingSessionId;
        StationId = stationId;
        StartedAtUtc = startedAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid GamingSessionId { get; private set; }
    public Guid StationId { get; private set; }
    public DateTime StartedAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }

    public void End(DateTime endedAtUtc)
    {
        RequireUtc(endedAtUtc);
        if (endedAtUtc < StartedAtUtc) throw new ArgumentOutOfRangeException(nameof(endedAtUtc));
        if (EndedAtUtc is not null && EndedAtUtc != endedAtUtc)
            throw new InvalidOperationException("A closed station segment is immutable.");
        EndedAtUtc = endedAtUtc;
    }

    private static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Segment timestamps must be UTC.");
    }
}
