using GameClub.Domain.Stations;

namespace GameClub.Domain.Users;

public sealed class PlayerAuthSession
{
    private PlayerAuthSession()
    {
    }

    public PlayerAuthSession(
        Guid id,
        Guid userId,
        Guid stationId,
        DateTime createdAtUtc,
        DateTime expiresAtUtc)
    {
        if (id == Guid.Empty || userId == Guid.Empty || stationId == Guid.Empty)
        {
            throw new ArgumentException("Session, user and station ids are required.");
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Session expiration must be later than creation time.");
        }

        Id = id;
        UserId = userId;
        StationId = stationId;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        ExpiresAtUtc = DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc);
        Status = PlayerAuthSessionStatus.Active;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid StationId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    public PlayerAuthSessionStatus Status { get; private set; }
    public User User { get; private set; } = null!;
    public Station Station { get; private set; } = null!;

    public bool IsActiveAt(DateTime utcNow) =>
        Status == PlayerAuthSessionStatus.Active && utcNow < ExpiresAtUtc;

    public void Transfer(Guid stationId, DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc || utcNow < CreatedAtUtc)
            throw new ArgumentException("Transfer time must be chronological UTC.", nameof(utcNow));
        if (!IsActiveAt(utcNow)) throw new InvalidOperationException("Only an active authorization can be transferred.");
        if (stationId == Guid.Empty || stationId == StationId)
            throw new ArgumentException("A different station is required.", nameof(stationId));
        StationId = stationId;
    }

    public bool ExpireIfNeeded(DateTime utcNow)
    {
        if (Status != PlayerAuthSessionStatus.Active || utcNow < ExpiresAtUtc)
        {
            return false;
        }

        Status = PlayerAuthSessionStatus.Expired;
        EndedAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return true;
    }

    public bool End(DateTime utcNow)
    {
        if (Status != PlayerAuthSessionStatus.Active)
        {
            return false;
        }

        if (utcNow >= ExpiresAtUtc)
        {
            return ExpireIfNeeded(utcNow);
        }

        Status = PlayerAuthSessionStatus.Ended;
        EndedAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return true;
    }
}
