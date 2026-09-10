namespace GameClub.Domain.Security;

public sealed class StationEnrollmentToken
{
    private StationEnrollmentToken()
    {
    }

    public StationEnrollmentToken(
        Guid id,
        string tokenHash,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        string? description)
    {
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        }

        Id = id;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public Guid Id { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }
    public bool Revoked { get; private set; }
    public string? Description { get; private set; }

    public bool TryUse(DateTime utcNow)
    {
        if (Revoked || UsedAtUtc is not null || utcNow >= ExpiresAtUtc)
        {
            return false;
        }

        UsedAtUtc = utcNow;
        return true;
    }

    public void Revoke() => Revoked = true;
}
