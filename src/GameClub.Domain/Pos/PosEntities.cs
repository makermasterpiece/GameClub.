using GameClub.Domain.Billing;

namespace GameClub.Domain.Pos;

public enum PaymentMethod { Cash, Card, Wallet }

internal static class PosGuard
{
    public static Guid Id(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("Identifier must be non-empty.");
    public static string Text(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max ? value.Trim() : throw new ArgumentException("Text is required and must be bounded.");
    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("Timestamp must be UTC.");
    public static decimal Positive(decimal value) => Money.RequireExactScale(value) > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    public static decimal Nonnegative(decimal value) => Money.RequireExactScale(value) >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed class ProductCategory
{
    private ProductCategory() { }
    public ProductCategory(Guid id, string name, bool isActive = true) { Id = PosGuard.Id(id); Update(name, isActive); }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; }
    public void Update(string name, bool isActive) { Name = PosGuard.Text(name, 100); IsActive = isActive; }
}

public sealed class Product
{
    public const int MaximumStock = 1_000_000;
    private Product() { }
    public Product(Guid id, Guid? categoryId, string name, decimal price, int stockQuantity, bool isActive = true)
    { Id = PosGuard.Id(id); Update(categoryId, name, price, isActive); AdjustStock(stockQuantity); }
    public Guid Id { get; private set; }
    public Guid? CategoryId { get; private set; }
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }
    public int StockQuantity { get; private set; }
    public bool IsActive { get; private set; }
    public void Update(Guid? categoryId, string name, decimal price, bool isActive)
    { if (categoryId == Guid.Empty) throw new ArgumentException("Invalid category."); CategoryId = categoryId; Name = PosGuard.Text(name, 200); Price = PosGuard.Positive(price); IsActive = isActive; }
    public void AdjustStock(int delta)
    { var quantity = (long)StockQuantity + delta; if (quantity is < 0 or > MaximumStock) throw new ArgumentOutOfRangeException(nameof(delta)); StockQuantity = (int)quantity; }
}

public sealed class EmployeeShift
{
    private EmployeeShift() { }
    public EmployeeShift(Guid id, Guid employeeId, DateTime openedAtUtc, decimal openingCash)
    { Id = PosGuard.Id(id); EmployeeId = PosGuard.Id(employeeId); OpenedAtUtc = PosGuard.Utc(openedAtUtc); OpeningCash = PosGuard.Nonnegative(openingCash); }
    public Guid Id { get; private set; }
    public Guid EmployeeId { get; private set; }
    public DateTime OpenedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public decimal OpeningCash { get; private set; }
    public decimal? ClosingCash { get; private set; }
    public Guid? CloseOperationId { get; private set; }
    public string? CloseFingerprint { get; private set; }
    public decimal? GamingSalesSnapshot { get; private set; }
    public void Close(decimal closingCash, Guid operationId, string fingerprint, DateTime utc, decimal gamingSales = 0)
    { if (ClosedAtUtc != null || utc < OpenedAtUtc) throw new InvalidOperationException("Shift cannot be closed."); ClosingCash = PosGuard.Nonnegative(closingCash); CloseOperationId = PosGuard.Id(operationId); CloseFingerprint = PosGuard.Text(fingerprint, 64); ClosedAtUtc = PosGuard.Utc(utc); GamingSalesSnapshot = PosGuard.Nonnegative(gamingSales); }
}

public sealed class Sale
{
    private Sale() { }
    public Sale(Guid id, Guid employeeShiftId, Guid employeeId, Guid? userId, Guid? stationId, PaymentMethod paymentMethod, decimal capturedTotal, DateTime createdAtUtc, string requestFingerprint)
    { Id = PosGuard.Id(id); EmployeeShiftId = PosGuard.Id(employeeShiftId); EmployeeId = PosGuard.Id(employeeId); if (userId == Guid.Empty || stationId == Guid.Empty || !Enum.IsDefined(paymentMethod) || paymentMethod == PaymentMethod.Wallet && userId == null) throw new ArgumentException("Invalid sale references or payment method."); UserId = userId; StationId = stationId; PaymentMethod = paymentMethod; CapturedTotal = PosGuard.Positive(capturedTotal); CreatedAtUtc = PosGuard.Utc(createdAtUtc); RequestFingerprint = PosGuard.Text(requestFingerprint, 64); }
    public Guid Id { get; private set; }
    public Guid EmployeeShiftId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? StationId { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }
    public decimal CapturedTotal { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string RequestFingerprint { get; private set; } = "";
}

public sealed class SaleItem
{
    private SaleItem() { }
    public SaleItem(Guid id, Guid saleId, Guid productId, string nameSnapshot, decimal unitPrice, int quantity)
    { Id = PosGuard.Id(id); SaleId = PosGuard.Id(saleId); ProductId = PosGuard.Id(productId); NameSnapshot = PosGuard.Text(nameSnapshot, 200); UnitPrice = PosGuard.Positive(unitPrice); if (quantity is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(quantity)); Quantity = quantity; LineTotal = PosGuard.Positive(unitPrice * quantity); }
    public Guid Id { get; private set; }
    public Guid SaleId { get; private set; }
    public Guid ProductId { get; private set; }
    public string NameSnapshot { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal { get; private set; }
}

public sealed class Payment
{
    private Payment() { }
    public Payment(Guid id, Guid saleId, Guid? refundId, Guid shiftId, Guid employeeId, PaymentMethod method, decimal amount, DateTime createdAtUtc)
    { Id = PosGuard.Id(id); SaleId = PosGuard.Id(saleId); if (refundId == Guid.Empty || method is not (PaymentMethod.Cash or PaymentMethod.Card) || amount == 0 || (refundId == null) != (amount > 0)) throw new ArgumentException("Invalid payment."); RefundId = refundId; ShiftId = PosGuard.Id(shiftId); EmployeeId = PosGuard.Id(employeeId); Method = method; Amount = Money.RequireExactScale(amount); CreatedAtUtc = PosGuard.Utc(createdAtUtc); }
    public Guid Id { get; private set; }
    public Guid SaleId { get; private set; }
    public Guid? RefundId { get; private set; }
    public Guid ShiftId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public PaymentMethod Method { get; private set; }
    public decimal Amount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}

public sealed class SaleRefund
{
    private SaleRefund() { }
    public SaleRefund(Guid id, Guid saleId, Guid employeeShiftId, Guid employeeId, decimal amount, string reason, DateTime createdAtUtc, string requestFingerprint)
    { Id = PosGuard.Id(id); SaleId = PosGuard.Id(saleId); EmployeeShiftId = PosGuard.Id(employeeShiftId); EmployeeId = PosGuard.Id(employeeId); Amount = PosGuard.Positive(amount); Reason = PosGuard.Text(reason, 500); CreatedAtUtc = PosGuard.Utc(createdAtUtc); RequestFingerprint = PosGuard.Text(requestFingerprint, 64); }
    public Guid Id { get; private set; }
    public Guid SaleId { get; private set; }
    public Guid EmployeeShiftId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public decimal Amount { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }
    public string RequestFingerprint { get; private set; } = "";
}

public sealed class PosOperation
{
    private PosOperation() { }
    public PosOperation(Guid id, string kind, string fingerprint, Guid entityId)
    { Id = PosGuard.Id(id); Kind = PosGuard.Text(kind, 40); Fingerprint = PosGuard.Text(fingerprint, 64); EntityId = PosGuard.Id(entityId); }
    public Guid Id { get; private set; }
    public string Kind { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public Guid EntityId { get; private set; }
}

public sealed class StockMovement
{
    private StockMovement() { }
    public StockMovement(Guid id, Guid productId, int delta, string reason, Guid employeeId, DateTime createdAtUtc)
    { Id = PosGuard.Id(id); ProductId = PosGuard.Id(productId); if (delta is 0 or < -Product.MaximumStock or > Product.MaximumStock) throw new ArgumentOutOfRangeException(nameof(delta)); Delta = delta; Reason = PosGuard.Text(reason, 500); EmployeeId = PosGuard.Id(employeeId); CreatedAtUtc = PosGuard.Utc(createdAtUtc); }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public int Delta { get; private set; }
    public string Reason { get; private set; } = "";
    public Guid EmployeeId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}
