namespace GameClub.Domain.Billing;

public sealed class Wallet
{
    private Wallet() { }

    public Wallet(Guid id, Guid userId, DateTime now)
    {
        if (id == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Wallet and user identifiers are required.");
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("Wallet timestamps must be UTC.", nameof(now));
        Id = id;
        UserId = userId;
        CreatedAtUtc = now;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public decimal Balance { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Apply(decimal signedAmount, bool allowNegative = false)
    {
        Money.RequireExactScale(signedAmount);
        var updated = Money.RequireExactScale(Balance + signedAmount);
        // Disabling credit must not erase existing debt or prevent positive repayments of it.
        if (!allowNegative && signedAmount < 0 && updated < 0)
            throw new InvalidOperationException("Insufficient wallet balance.");
        Balance = updated;
    }
}
