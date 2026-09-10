using GameClub.Domain.Stations;

namespace GameClub.Domain.Security;

public sealed class StationCredential
{
    private StationCredential()
    {
    }

    public StationCredential(
        Guid id,
        Guid stationId,
        string secretHash,
        string protectedSecret,
        DateTime createdAtUtc)
    {
        Id = id;
        StationId = stationId;
        SecretHash = secretHash;
        ProtectedSecret = protectedSecret;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid StationId { get; private set; }
    public string SecretHash { get; private set; } = string.Empty;

    // HMAC verification needs key material. It is encrypted by ASP.NET Data Protection;
    // plaintext station secrets are never persisted in PostgreSQL.
    public string ProtectedSecret { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }
    public Station Station { get; private set; } = null!;

    public bool IsActive => RevokedAtUtc is null;
    public void MarkUsed(DateTime utcNow) => LastUsedAtUtc = utcNow;
    public bool Revoke(DateTime utcNow)
    {
        if (RevokedAtUtc is not null)
        {
            return false;
        }

        RevokedAtUtc = utcNow;
        return true;
    }
}
