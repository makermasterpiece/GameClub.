namespace GameClub.Domain.Billing;

public sealed class Tariff
{
    public const decimal MaximumPrice = 9999999999999999.99m;
    private Tariff() { }

    public Tariff(Guid id, string name, Guid stationGroupId, decimal hourlyPrice, bool isActive = true)
    {
        if (id == Guid.Empty || stationGroupId == Guid.Empty)
            throw new ArgumentException("Tariff and group ids are required.");
        Id = id;
        Name = StationGroup.ValidateName(name);
        StationGroupId = stationGroupId;
        UpdatePrice(hourlyPrice);
        IsActive = isActive;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid StationGroupId { get; private set; }
    public decimal HourlyPrice { get; private set; }
    public bool IsActive { get; private set; }

    public void UpdatePrice(decimal hourlyPrice)
    {
        ValidatePrice(hourlyPrice);
        HourlyPrice = hourlyPrice;
    }

    public void SetActive(bool active) => IsActive = active;

    internal static void ValidatePrice(decimal price)
    {
        if (price <= 0 || price > MaximumPrice || decimal.Round(price, 2) != price)
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive decimal(18,2).");
    }
}
