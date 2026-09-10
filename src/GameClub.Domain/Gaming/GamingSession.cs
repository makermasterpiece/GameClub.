using GameClub.Domain.Billing;
using SessionBillingMode = GameClub.Domain.Billing.BillingMode;

namespace GameClub.Domain.Gaming;

public sealed class GamingSession
{
    public const int MaximumPurchasedMinutes = 7 * 24 * 60;

    private GamingSession()
    {
    }

    public GamingSession(Guid id, Guid userId, Guid stationId, int? purchasedMinutes, DateTime now)
    {
        if (id == Guid.Empty || userId == Guid.Empty || stationId == Guid.Empty)
        {
            throw new ArgumentException("Session, user and station identifiers are required.");
        }

        RequireUtc(now);
        if (purchasedMinutes is < 1 or > MaximumPurchasedMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(purchasedMinutes));
        }

        Id = id;
        UserId = userId;
        StationId = stationId;
        PurchasedMinutes = purchasedMinutes;
        CreatedAtUtc = now;
        LastStateChangedAtUtc = now;
        Status = GamingSessionStatus.Created;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid StationId { get; private set; }
    public Guid? TariffId { get; private set; }
    public Guid? PackageId { get; private set; }
    public GamingSessionStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? ExpectedEndAtUtc { get; private set; }
    public DateTime? WindowEndsAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    public int? PurchasedMinutes { get; private set; }
    public int AddedMinutes { get; private set; }
    public decimal? InitialPrice { get; private set; }
    public decimal? FinalPrice { get; private set; }
    public SessionBillingMode? BillingMode { get; private set; }
    public decimal? HourlyPriceSnapshot { get; private set; }
    public decimal? PrepaidCharged { get; private set; }
    public decimal? PackagePriceSnapshot { get; private set; }
    public int? PackageDurationMinutesSnapshot { get; private set; }
    public int? FundingLimitSeconds { get; private set; }
    public string? PurchaseFingerprint { get; private set; }
    public long AccumulatedTicks { get; private set; }
    public long AccumulatedSeconds => AccumulatedTicks / TimeSpan.TicksPerSecond;
    public DateTime? LastResumedAtUtc { get; private set; }
    public DateTime LastStateChangedAtUtc { get; private set; }

    public void Start(DateTime now)
    {
        RequireTransitionTime(now);
        RequireStatus(GamingSessionStatus.Created);
        if (WindowEndsAtUtc <= now) throw new InvalidOperationException("The package window has ended.");
        var deadline = CalculateDeadline(now);
        StartedAtUtc = now;
        LastResumedAtUtc = now;
        ExpectedEndAtUtc = deadline;
        LastStateChangedAtUtc = now;
        Status = GamingSessionStatus.Active;
    }

    public void Pause(DateTime now)
    {
        RequireTransitionTime(now);
        RequireStatus(GamingSessionStatus.Active);
        RequireNotExpired(now);
        AccumulatedTicks = ElapsedTicks(now);
        LastResumedAtUtc = null;
        ExpectedEndAtUtc = null;
        LastStateChangedAtUtc = now;
        Status = GamingSessionStatus.Paused;
    }

    public void Resume(DateTime now)
    {
        RequireTransitionTime(now);
        RequireStatus(GamingSessionStatus.Paused);
        RequireNotExpired(now);
        var deadline = CalculateDeadline(now);
        LastResumedAtUtc = now;
        ExpectedEndAtUtc = deadline;
        LastStateChangedAtUtc = now;
        Status = GamingSessionStatus.Active;
    }

    public bool End(DateTime now)
    {
        RequireUtc(now);
        if (Status is GamingSessionStatus.Completed or GamingSessionStatus.Cancelled)
        {
            return false;
        }

        RequireTransitionTime(now);
        var endedAtUtc = Status == GamingSessionStatus.Active && ExpectedEndAtUtc is { } deadline && deadline < now
            ? deadline
            : now;
        if (WindowEndsAtUtc is { } windowEnd && windowEnd < endedAtUtc) endedAtUtc = windowEnd;
        AccumulatedTicks = ElapsedTicks(now);
        LastResumedAtUtc = null;
        EndedAtUtc = endedAtUtc;
        LastStateChangedAtUtc = now;
        Status = Status == GamingSessionStatus.Created
            ? GamingSessionStatus.Cancelled
            : GamingSessionStatus.Completed;
        return true;
    }

    public bool IsExpiredAt(DateTime now)
    {
        RequireUtc(now);
        return (Status == GamingSessionStatus.Active && ExpectedEndAtUtc <= now) ||
            (Status is GamingSessionStatus.Active or GamingSessionStatus.Paused && WindowEndsAtUtc <= now);
    }

    public long ElapsedSeconds(DateTime now) => ElapsedTicks(now) / TimeSpan.TicksPerSecond;

    public long RemainingBudgetTicks(DateTime now) => HasTimeBudget()
        ? Math.Max(0, BudgetTicks() - ElapsedTicks(now))
        : throw new InvalidOperationException("An unlimited session has no funded budget.");

    public int? RemainingSeconds(DateTime now)
    {
        RequireUtc(now);
        if (!HasTimeBudget())
        {
            return null;
        }

        if (Status is GamingSessionStatus.Completed or GamingSessionStatus.Cancelled)
        {
            return 0;
        }

        var remainingTicks = Math.Max(0, BudgetTicks() - ElapsedTicks(now));
        if (WindowEndsAtUtc is { } windowEnd)
            remainingTicks = Math.Min(remainingTicks, Math.Max(0, (windowEnd - now).Ticks));
        return checked((int)((remainingTicks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond));
    }

    public void Extend(int minutes, DateTime now)
    {
        RequireTransitionTime(now);
        RequireOpenSession();
        RequireNotExpired(now);
        if (PurchasedMinutes is null && FundingLimitSeconds is null)
        {
            throw new InvalidOperationException("An unlimited session does not have a time budget to extend.");
        }

        if (minutes < 1 || (PurchasedMinutes is { } purchased
                ? (long)purchased + AddedMinutes + minutes > MaximumPurchasedMinutes
                : (long)FundingLimitSeconds!.Value + (long)minutes * 60 > MaximumPurchasedMinutes * 60))
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        var deadline = ExpectedEndAtUtc?.AddMinutes(minutes);
        if (deadline is { } extended && WindowEndsAtUtc is { } windowEnd && extended > windowEnd) deadline = windowEnd;
        AddedMinutes += minutes;
        if (FundingLimitSeconds is not null) FundingLimitSeconds += checked(minutes * 60);
        ExpectedEndAtUtc = deadline;
        LastStateChangedAtUtc = now;
    }

    public void Transfer(Guid stationId, DateTime now)
    {
        RequireTransitionTime(now);
        RequireOpenSession();
        RequireNotExpired(now);
        if (stationId == Guid.Empty || stationId == StationId)
        {
            throw new ArgumentException("A different, non-empty station identifier is required.", nameof(stationId));
        }

        StationId = stationId;
        LastStateChangedAtUtc = now;
    }

    public void SetPricing(Guid? tariffId, Guid? packageId, decimal initialPrice)
    {
        RequireStatus(GamingSessionStatus.Created);
        if (tariffId == Guid.Empty || packageId == Guid.Empty)
        {
            throw new ArgumentException("Optional pricing identifiers must be non-empty.");
        }

        if (initialPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialPrice));
        }

        TariffId = tariffId;
        PackageId = packageId;
        InitialPrice = initialPrice;
    }

    public void ConfigureBilling(SessionBillingMode mode, decimal? hourlyPriceSnapshot, decimal? prepaidCharged,
        int? fundingLimitSeconds, string purchaseFingerprint)
    {
        RequireStatus(GamingSessionStatus.Created);
        if (!Enum.IsDefined(mode) || InitialPrice is null || (TariffId is null && PackageId is null))
            throw new ArgumentException("Valid pricing is required before configuring billing.");
        if (PurchaseFingerprint is not null)
            throw new InvalidOperationException("Billing configuration is immutable.");
        if (purchaseFingerprint is not { Length: 64 } || purchaseFingerprint.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A SHA-256 purchase fingerprint is required.", nameof(purchaseFingerprint));
        if (mode == SessionBillingMode.Prepaid)
        {
            if (PurchasedMinutes is null || prepaidCharged is null or < 0 || fundingLimitSeconds is not null)
                throw new ArgumentException("Prepaid sessions require a purchased duration and charge.");
            Money.RequireExactScale(prepaidCharged.Value);
        }
        else if (PurchasedMinutes is not null || prepaidCharged is not null || hourlyPriceSnapshot is null or <= 0 ||
                 fundingLimitSeconds is null or < 1 or > MaximumPurchasedMinutes * 60)
            throw new ArgumentException("Postpaid sessions require a rate and a bounded funding duration.");
        if (hourlyPriceSnapshot is not null)
        {
            if (hourlyPriceSnapshot <= 0) throw new ArgumentOutOfRangeException(nameof(hourlyPriceSnapshot));
            Money.RequireExactScale(hourlyPriceSnapshot.Value);
        }

        BillingMode = mode;
        HourlyPriceSnapshot = hourlyPriceSnapshot;
        PrepaidCharged = prepaidCharged;
        FundingLimitSeconds = fundingLimitSeconds;
        PurchaseFingerprint = purchaseFingerprint;
    }

    public void SetWindowEnd(DateTime windowEndsAtUtc)
    {
        RequireStatus(GamingSessionStatus.Created);
        RequireUtc(windowEndsAtUtc);
        if (windowEndsAtUtc <= CreatedAtUtc) throw new ArgumentOutOfRangeException(nameof(windowEndsAtUtc));
        if (WindowEndsAtUtc is not null) throw new InvalidOperationException("The package window is immutable.");
        WindowEndsAtUtc = windowEndsAtUtc;
    }

    public void SetPackageSnapshot(decimal price, int durationMinutes)
    {
        RequireStatus(GamingSessionStatus.Created);
        if (PackageId is null || PackagePriceSnapshot is not null)
            throw new InvalidOperationException("Package pricing must be configured once.");
        Money.RequireExactScale(price);
        if (price <= 0 || durationMinutes is < 1 or > MaximumPurchasedMinutes)
            throw new ArgumentOutOfRangeException(nameof(price));
        PackagePriceSnapshot = price;
        PackageDurationMinutesSnapshot = durationMinutes;
    }

    public void AddPrepaidCharge(decimal charge)
    {
        RequireOpenSession();
        Money.RequireExactScale(charge);
        if (charge < 0 || BillingMode != SessionBillingMode.Prepaid || PrepaidCharged is null)
            throw new InvalidOperationException("An additional prepaid charge requires a prepaid session.");
        var total = PrepaidCharged.Value + charge;
        Money.RequireExactScale(total);
        PrepaidCharged = total;
    }

    public void SetFinalPrice(decimal finalPrice)
    {
        if (Status is not (GamingSessionStatus.Completed or GamingSessionStatus.Cancelled))
        {
            throw new InvalidOperationException("Final price is only available for an ended session.");
        }

        if (finalPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalPrice));
        }

        if (FinalPrice is not null && FinalPrice != finalPrice)
        {
            throw new InvalidOperationException("A finalized session price cannot be changed.");
        }

        FinalPrice = finalPrice;
    }

    private bool HasTimeBudget() => PurchasedMinutes is not null || FundingLimitSeconds is not null;

    private long BudgetTicks() => PurchasedMinutes is { } minutes
        ? checked(((long)minutes + AddedMinutes) * TimeSpan.TicksPerMinute)
        : checked((long)FundingLimitSeconds!.Value * TimeSpan.TicksPerSecond);

    private long ElapsedTicks(DateTime now)
    {
        RequireUtc(now);
        var ticks = AccumulatedTicks;
        if (Status == GamingSessionStatus.Active && LastResumedAtUtc is { } resumedAtUtc)
        {
            var countedUntil = WindowEndsAtUtc is { } windowEnd && windowEnd < now ? windowEnd : now;
            ticks = checked(ticks + Math.Max(0, (countedUntil - resumedAtUtc).Ticks));
        }

        return HasTimeBudget() ? Math.Min(ticks, BudgetTicks()) : ticks;
    }

    private DateTime? CalculateDeadline(DateTime now)
    {
        var budgetEnd = HasTimeBudget() ? now.AddTicks(Math.Max(0, BudgetTicks() - AccumulatedTicks)) : (DateTime?)null;
        return WindowEndsAtUtc is { } windowEnd && (budgetEnd is null || windowEnd < budgetEnd)
            ? windowEnd : budgetEnd;
    }

    private void RequireTransitionTime(DateTime now)
    {
        RequireUtc(now);
        if (now < LastStateChangedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Session changes must be chronological.");
        }
    }

    private static void RequireUtc(DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Session timestamps must be UTC.", nameof(now));
        }
    }

    private void RequireStatus(GamingSessionStatus required)
    {
        if (Status != required)
        {
            throw new InvalidOperationException($"Session must be {required} for this operation.");
        }
    }

    private void RequireOpenSession()
    {
        if (Status is not (GamingSessionStatus.Active or GamingSessionStatus.Paused))
        {
            throw new InvalidOperationException("Session must be active or paused for this operation.");
        }
    }

    private void RequireNotExpired(DateTime now)
    {
        if (IsExpiredAt(now))
        {
            throw new InvalidOperationException("An expired session must be ended before further operations.");
        }
    }
}
