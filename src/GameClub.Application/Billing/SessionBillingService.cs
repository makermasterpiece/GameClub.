using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GameClub.Application.Abstractions;
using GameClub.Application.Gaming;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Billing;

public sealed record SessionPurchase(Guid OperationId, Guid UserId, Guid StationId, BillingMode Mode,
    Guid? TariffId, Guid? PackageId, int? PurchasedMinutes, decimal? PostpaidLimit);

public sealed class SessionBillingOptions
{
    public string ClubTimeZoneId { get; set; } = "UTC";
}

/// <summary>Billing operations join the caller's transaction; no operation commits independently.</summary>
public sealed class SessionBillingService(IClubData data, WalletService wallets, SessionBillingOptions options)
{
    private readonly TimeZoneInfo _clubTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.ClubTimeZoneId);

    public static string Fingerprint(SessionPurchase purchase)
    {
        if (purchase.OperationId == Guid.Empty || purchase.UserId == Guid.Empty || purchase.StationId == Guid.Empty ||
            purchase.TariffId == Guid.Empty || purchase.PackageId == Guid.Empty || !Enum.IsDefined(purchase.Mode))
            throw new ClubException("INVALID_PURCHASE");
        if (purchase.PurchasedMinutes is < 1 or > GamingSession.MaximumPurchasedMinutes || purchase.PostpaidLimit is <= 0)
            throw new ClubException("INVALID_PURCHASE");
        if (purchase.PostpaidLimit is { } limit && Money.Round(limit) != limit)
            throw new ClubException("INVALID_AMOUNT");

        var text = string.Join('|', purchase.UserId.ToString("D"), purchase.StationId.ToString("D"),
            ((int)purchase.Mode).ToString(CultureInfo.InvariantCulture), purchase.TariffId?.ToString("D") ?? "-",
            purchase.PackageId?.ToString("D") ?? "-", purchase.PurchasedMinutes?.ToString(CultureInfo.InvariantCulture) ?? "-",
            purchase.PostpaidLimit?.ToString("G29", CultureInfo.InvariantCulture) ?? "-");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public async Task<GamingSession> PurchaseCoreAsync(SessionPurchase purchase, Station station, DateTime now, DateTime authExpiresAtUtc,
        Guid? employeeId, CancellationToken ct)
    {
        var fingerprint = Fingerprint(purchase);
        if (purchase.TariffId is null && purchase.PackageId is null) throw new ClubException("PRICING_REQUIRED");
        if (purchase.TariffId is not null && purchase.PackageId is not null) throw new ClubException("AMBIGUOUS_PRICING");

        Tariff? tariff = null;
        TariffPackage? package = null;
        if (purchase.TariffId is { } tariffId)
        {
            tariff = await data.Query<Tariff>().SingleOrDefaultAsync(t => t.Id == tariffId, ct);
            if (tariff is null || !tariff.IsActive) throw new ClubException("TARIFF_UNAVAILABLE");
            if (tariff.StationGroupId != station.StationGroupId) throw new ClubException("STATION_GROUP_MISMATCH");
        }
        if (purchase.PackageId is { } packageId)
        {
            package = await data.Query<TariffPackage>().SingleOrDefaultAsync(p => p.Id == packageId, ct);
            if (package is null || !package.IsActive) throw new ClubException("PACKAGE_UNAVAILABLE");
            if (package.StationGroupId != station.StationGroupId) throw new ClubException("STATION_GROUP_MISMATCH");
        }

        if (purchase.Mode == BillingMode.Prepaid)
        {
            if (purchase.PostpaidLimit is not null) throw new ClubException("INVALID_PURCHASE");
            int minutes;
            decimal charge;
            DateTime? windowEndUtc = null;
            if (package is not null)
            {
                if (purchase.PurchasedMinutes is not null) throw new ClubException("PACKAGE_DURATION_FIXED");
                try
                {
                    var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, _clubTimeZone);
                    minutes = package.GetPurchasedMinutes(localNow);
                    if (package.GetWindowEndLocal(localNow) is { } localEnd)
                    {
                        windowEndUtc = ResolveWindowEndUtc(localEnd, _clubTimeZone);
                        minutes = Math.Min(minutes, (int)Math.Floor((windowEndUtc.Value - now).TotalMinutes));
                        if (minutes < 1) throw new InvalidOperationException("Package window has ended.");
                    }
                }
                catch (InvalidOperationException) { throw new ClubException("PACKAGE_UNAVAILABLE"); }
                charge = package.Price;
            }
            else
            {
                minutes = purchase.PurchasedMinutes ?? throw new ClubException("DURATION_REQUIRED");
                charge = Money.Round(tariff!.HourlyPrice * minutes / 60m);
            }

            if (now.AddMinutes(minutes) > authExpiresAtUtc) throw new ClubException("SESSION_EXCEEDS_AUTH_LIFETIME");

            var session = new GamingSession(purchase.OperationId, purchase.UserId, station.Id, minutes, now);
            session.SetPricing(tariff?.Id, package?.Id, charge);
            if (package is not null) session.SetPackageSnapshot(package.Price, package.DurationMinutes);
            if (windowEndUtc is { } hardEnd) session.SetWindowEnd(hardEnd);
            session.ConfigureBilling(BillingMode.Prepaid, tariff?.HourlyPrice, charge, null, fingerprint);
            data.Add(session);
            await data.SaveAsync(ct);
            await wallets.ApplyCoreAsync(purchase.UserId, -charge, WalletTransactionType.GamingCharge,
                session.Id, "GamingSession", session.Id, employeeId, ct);
            return session;
        }

        if (package is not null || tariff is null || purchase.PurchasedMinutes is not null)
            throw new ClubException("INVALID_POSTPAID_PRICING");
        var wallet = await wallets.GetOrCreateCoreAsync(purchase.UserId, ct);
        var available = await wallets.GetAvailableCoreAsync(wallet, ct);
        var reserve = purchase.PostpaidLimit ?? available;
        if (reserve <= 0) throw new ClubException("INSUFFICIENT_FUNDS");
        var fundedSeconds = Math.Min(GamingSession.MaximumPurchasedMinutes * 60,
            Money.MaxAffordableSeconds(reserve, tariff.HourlyPrice));
        fundedSeconds = (int)Math.Min(fundedSeconds, Math.Max(0, Math.Floor((authExpiresAtUtc - now).TotalSeconds)));
        if (fundedSeconds < 1) throw new ClubException("INSUFFICIENT_FUNDS");
        var postpaid = new GamingSession(purchase.OperationId, purchase.UserId, station.Id, null, now);
        postpaid.SetPricing(tariff.Id, null, 0m);
        postpaid.ConfigureBilling(BillingMode.Postpaid, tariff.HourlyPrice, null, fundedSeconds, fingerprint);
        // Persist the session before its reservation FK; all writes remain in the same outer transaction.
        data.Add(postpaid);
        await data.SaveAsync(ct);
        await wallets.ReserveCoreAsync(purchase.UserId, postpaid.Id, reserve, ct);
        return postpaid;
    }

    public async Task SettleCoreAsync(GamingSession session, Guid? employeeId, CancellationToken ct)
    {
        if (session.BillingMode is null) return;
        if (session.BillingMode == BillingMode.Prepaid)
        {
            session.SetFinalPrice(session.PrepaidCharged ?? throw new ClubException("BILLING_STATE_INVALID"));
            return;
        }

        var charge = Money.Charge(session.HourlyPriceSnapshot ?? throw new ClubException("BILLING_STATE_INVALID"),
            session.AccumulatedTicks);
        await wallets.SettleCoreAsync(session.Id, charge, employeeId, ct);
        session.SetFinalPrice(charge);
    }

    public async Task<SessionExtensionQuote> QuoteExtensionCoreAsync(GamingSession session, int minutes,
        DateTime now, DateTime authExpiresAtUtc, CancellationToken ct)
    {
        if (session.Status is not (GamingSessionStatus.Active or GamingSessionStatus.Paused))
            throw new ClubException("INVALID_SESSION_TRANSITION");
        if (session.IsExpiredAt(now)) throw new ClubException("SESSION_EXPIRED");
        if (minutes is < 1 or > GamingSession.MaximumPurchasedMinutes)
            throw new ClubException("INVALID_DURATION");
        if (session.BillingMode is null) throw new ClubException("EXTENSION_PRICING_UNAVAILABLE");
        var totalSeconds = session.PurchasedMinutes is { } purchased
            ? ((long)purchased + session.AddedMinutes + minutes) * 60
            : (long)(session.FundingLimitSeconds ?? 0) + minutes * 60L;
        if (totalSeconds > GamingSession.MaximumPurchasedMinutes * 60)
            throw new ClubException("INVALID_DURATION");
        var effectiveEnd = now.AddTicks(session.RemainingBudgetTicks(now) + minutes * TimeSpan.TicksPerMinute);
        if (effectiveEnd > authExpiresAtUtc) throw new ClubException("SESSION_EXCEEDS_AUTH_LIFETIME");
        if (session.WindowEndsAtUtc is { } windowEnd && effectiveEnd > windowEnd)
            throw new ClubException("SESSION_EXCEEDS_PACKAGE_WINDOW");

        decimal charge = 0m;
        decimal additionalReservation = 0m;
        try
        {
            if (session.BillingMode == BillingMode.Prepaid)
            {
                var previouslyCharged = session.PrepaidCharged ?? throw new ClubException("BILLING_STATE_INVALID");
                charge = session.PackageId is not null
                    ? Money.Round((session.PackagePriceSnapshot ?? throw new ClubException("EXTENSION_PRICING_UNAVAILABLE")) *
                        (session.AddedMinutes + minutes) /
                        (session.PackageDurationMinutesSnapshot ?? throw new ClubException("EXTENSION_PRICING_UNAVAILABLE"))) -
                        (previouslyCharged - (session.InitialPrice ?? throw new ClubException("BILLING_STATE_INVALID")))
                    : Money.Round((session.HourlyPriceSnapshot ?? throw new ClubException("EXTENSION_PRICING_UNAVAILABLE")) *
                        (session.PurchasedMinutes!.Value + session.AddedMinutes + minutes) / 60m) - previouslyCharged;
                Money.RequireExactScale((session.PrepaidCharged ?? throw new ClubException("BILLING_STATE_INVALID")) + charge);
            }
            else
            {
                var reservation = await data.Query<WalletReservation>().SingleOrDefaultAsync(r => r.GamingSessionId == session.Id, ct)
                    ?? throw new ClubException("WALLET_RESERVATION_NOT_FOUND");
                if (reservation.ReleasedAtUtc is not null) throw new ClubException("RESERVATION_ALREADY_RELEASED");
                // Reserve up to the next cent for the complete new budget. Final billing still rounds once,
                // and existing unused funding is reused rather than charging rounded fragments of time.
                var rate = session.HourlyPriceSnapshot ?? throw new ClubException("BILLING_STATE_INVALID");
                var requiredHold = decimal.Ceiling(rate * totalSeconds / 36m) / 100m;
                additionalReservation = Math.Max(0m, requiredHold - reservation.Amount);
                Money.RequireExactScale(additionalReservation);
            }
        }
        catch (ArgumentOutOfRangeException) { throw new ClubException("INVALID_AMOUNT"); }
        catch (OverflowException) { throw new ClubException("INVALID_AMOUNT"); }
        var wallet = await data.Query<Wallet>().SingleAsync(w => w.UserId == session.UserId, ct);
        await wallets.EnsureAvailableCoreAsync(wallet, charge + additionalReservation, ct);
        return new SessionExtensionQuote(session.Id, minutes, session.BillingMode.Value.ToString(), charge,
            additionalReservation, session.Status == GamingSessionStatus.Active ? effectiveEnd : null);
    }

    public async Task ExtendCoreAsync(GamingSession session, Guid operationId, SessionExtensionQuote quote,
        DateTime now, Guid? employeeId, CancellationToken ct)
    {
        if (session.BillingMode == BillingMode.Prepaid)
        {
            await wallets.ApplyCoreAsync(session.UserId, -quote.Charge, WalletTransactionType.GamingCharge,
                operationId, "SessionExtension", session.Id, employeeId, ct);
            session.AddPrepaidCharge(quote.Charge);
        }
        else await wallets.IncreaseReservationCoreAsync(session.Id, quote.AdditionalReservation, ct);
        session.Extend(quote.Minutes, now);
    }

    public static DateTime ResolveWindowEndUtc(DateTime localEnd, TimeZoneInfo clubTimeZone)
    {
        localEnd = DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified);
        if (clubTimeZone.IsInvalidTime(localEnd)) throw new ClubException("PACKAGE_WINDOW_INVALID");
        if (clubTimeZone.IsAmbiguousTime(localEnd))
        {
            // Earliest occurrence is the conservative boundary when a clock repeats during fall-back.
            var earliestOffset = clubTimeZone.GetAmbiguousTimeOffsets(localEnd).Max();
            return new DateTimeOffset(localEnd, earliestOffset).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(localEnd, clubTimeZone);
    }
}
