namespace GameClub.Domain.Billing;

public sealed class TariffPackage
{
    public const int MaximumDurationMinutes = 7 * 24 * 60;
    private TariffPackage() { }

    public TariffPackage(Guid id, string name, Guid stationGroupId, int durationMinutes,
        decimal price, TimeOnly? availableFrom = null, TimeOnly? availableUntil = null, bool isActive = true)
    {
        if (id == Guid.Empty || stationGroupId == Guid.Empty)
            throw new ArgumentException("Package and group ids are required.");
        if (durationMinutes is < 1 or > MaximumDurationMinutes)
            throw new ArgumentOutOfRangeException(nameof(durationMinutes));
        if (availableFrom.HasValue != availableUntil.HasValue ||
            (availableFrom.HasValue && availableFrom == availableUntil))
            throw new ArgumentException("An availability window needs two distinct endpoints.");
        Tariff.ValidatePrice(price);
        Id = id;
        Name = StationGroup.ValidateName(name);
        StationGroupId = stationGroupId;
        DurationMinutes = durationMinutes;
        Price = price;
        AvailableFrom = availableFrom;
        AvailableUntil = availableUntil;
        IsActive = isActive;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid StationGroupId { get; private set; }
    public int DurationMinutes { get; private set; }
    public decimal Price { get; private set; }
    public TimeOnly? AvailableFrom { get; private set; }
    public TimeOnly? AvailableUntil { get; private set; }
    public bool IsActive { get; private set; }

    public void SetActive(bool active) => IsActive = active;

    public int GetPurchasedMinutes(DateTime clubLocalNow)
    {
        var windowEnd = GetWindowEndLocal(clubLocalNow);
        if (windowEnd is null) return DurationMinutes;

        // Whole-minute entitlement is capped at the window end. The fixed package price is
        // deliberately not prorated for a late purchase; callers show this before purchase.
        var minutes = Math.Min(DurationMinutes, (int)Math.Floor((windowEnd.Value - clubLocalNow).TotalMinutes));
        if (minutes < 1) throw new InvalidOperationException("PACKAGE_UNAVAILABLE");
        return minutes;
    }

    public DateTime? GetWindowEndLocal(DateTime clubLocalNow)
    {
        if (!IsActive) throw new InvalidOperationException("PACKAGE_INACTIVE");
        if (AvailableFrom is not { } from || AvailableUntil is not { } until) return null;
        var time = TimeOnly.FromDateTime(clubLocalNow);
        DateTime windowEnd;
        if (from < until)
        {
            if (time < from || time >= until) throw new InvalidOperationException("PACKAGE_UNAVAILABLE");
            windowEnd = clubLocalNow.Date.Add(until.ToTimeSpan());
        }
        else
        {
            if (time >= from) windowEnd = clubLocalNow.Date.AddDays(1).Add(until.ToTimeSpan());
            else if (time < until) windowEnd = clubLocalNow.Date.Add(until.ToTimeSpan());
            else throw new InvalidOperationException("PACKAGE_UNAVAILABLE");
        }

        return DateTime.SpecifyKind(windowEnd, DateTimeKind.Unspecified);
    }
}
