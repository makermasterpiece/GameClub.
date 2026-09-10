namespace GameClub.Domain.Billing;

public sealed class WalletReservation
{
    private WalletReservation() { }

    public WalletReservation(Guid id, Guid walletId, Guid gamingSessionId, decimal amount, DateTime now)
    {
        if (id == Guid.Empty || walletId == Guid.Empty || gamingSessionId == Guid.Empty)
            throw new ArgumentException("Reservation, wallet and session identifiers are required.");
        Money.RequireExactScale(amount);
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("Reservation timestamps must be UTC.", nameof(now));
        Id = id;
        WalletId = walletId;
        GamingSessionId = gamingSessionId;
        Amount = amount;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public Guid GamingSessionId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ReleasedAtUtc { get; private set; }

    public void Increase(decimal amount)
    {
        Money.RequireExactScale(amount);
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (ReleasedAtUtc is not null) throw new InvalidOperationException("A released reservation cannot be increased.");
        var total = Amount + amount;
        Money.RequireExactScale(total);
        Amount = total;
    }

    public bool Release(DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc || now < CreatedAtUtc)
            throw new ArgumentException("Release time must be UTC and no earlier than reservation creation.", nameof(now));
        if (ReleasedAtUtc is not null) return false;
        ReleasedAtUtc = now;
        return true;
    }
}
