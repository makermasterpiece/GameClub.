namespace GameClub.Domain.Billing;

public enum WalletTransactionType
{
    Deposit,
    GamingCharge,
    Refund,
    ProductPurchase,
    Bonus,
    Adjustment
}

public sealed class WalletTransaction
{
    public const int MaximumReferenceTypeLength = 50;
    private WalletTransaction() { }

    public WalletTransaction(Guid id, Guid walletId, WalletTransactionType type, decimal amount,
        decimal balanceAfter, DateTime createdAtUtc, string? referenceType, Guid? referenceId,
        Guid operationId, Guid? employeeId = null)
    {
        if (id == Guid.Empty || walletId == Guid.Empty || operationId == Guid.Empty ||
            referenceId == Guid.Empty || employeeId == Guid.Empty)
            throw new ArgumentException("Wallet transaction identifiers must be non-empty.");
        if (createdAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Transaction timestamps must be UTC.", nameof(createdAtUtc));
        ValidateAmount(type, amount);
        Money.RequireExactScale(balanceAfter);
        var normalizedReference = string.IsNullOrWhiteSpace(referenceType) ? null : referenceType.Trim();
        if (normalizedReference?.Length > MaximumReferenceTypeLength || referenceId is not null && normalizedReference is null)
            throw new ArgumentException("A bounded reference type is required when a reference id is present.", nameof(referenceType));
        Id = id;
        WalletId = walletId;
        Type = type;
        Amount = amount;
        BalanceAfter = balanceAfter;
        CreatedAtUtc = createdAtUtc;
        ReferenceType = normalizedReference;
        ReferenceId = referenceId;
        OperationId = operationId;
        EmployeeId = employeeId;
    }

    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public WalletTransactionType Type { get; private set; }
    public decimal Amount { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? ReferenceType { get; private set; }
    public Guid? ReferenceId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid? EmployeeId { get; private set; }

    public static void ValidateAmount(WalletTransactionType type, decimal amount)
    {
        Money.RequireExactScale(amount);
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        var valid = type switch
        {
            WalletTransactionType.Deposit or WalletTransactionType.Refund or WalletTransactionType.Bonus => amount > 0,
            WalletTransactionType.GamingCharge => amount <= 0,
            WalletTransactionType.ProductPurchase => amount < 0,
            WalletTransactionType.Adjustment => amount != 0,
            _ => false
        };
        if (!valid) throw new ArgumentOutOfRangeException(nameof(amount), "The amount's sign must match its ledger transaction type.");
    }
}
