using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Users;
using Xunit;

namespace GameClub.Server.Tests.Gaming;

public sealed class SessionOperationsTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PostpaidExtension_PreservesElapsedAndIncreasesFundingOnlyOnce()
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now);
        session.SetPricing(Guid.NewGuid(), null, 0m);
        session.ConfigureBilling(BillingMode.Postpaid, 6m, null, 600, new string('A', 64));
        session.Start(Now);
        session.Extend(5, Now.AddSeconds(30));
        Assert.Equal(900, session.FundingLimitSeconds);
        Assert.Equal(5, session.AddedMinutes);
        Assert.Equal(30, session.ElapsedSeconds(Now.AddSeconds(30)));
        Assert.Equal(Now.AddMinutes(15), session.ExpectedEndAtUtc);
        Assert.Equal(870, session.RemainingSeconds(Now.AddSeconds(30)));
    }

    [Fact]
    public void PausedPostpaidExtension_DoesNotRestartClock()
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now);
        session.SetPricing(Guid.NewGuid(), null, 0m);
        session.ConfigureBilling(BillingMode.Postpaid, 6m, null, 600, new string('A', 64));
        session.Start(Now);
        session.Pause(Now.AddSeconds(30));
        session.Extend(5, Now.AddMinutes(5));
        Assert.Null(session.ExpectedEndAtUtc);
        Assert.Equal(30, session.ElapsedSeconds(Now.AddHours(1)));
        session.Resume(Now.AddMinutes(10));
        Assert.Equal(Now.AddMinutes(24.5), session.ExpectedEndAtUtc);
    }

    [Fact]
    public void PackageSnapshot_IsImmutableAndPrepaidTotalAccumulates()
    {
        var session = new GamingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 60, Now);
        session.SetPricing(null, Guid.NewGuid(), 5m);
        session.SetPackageSnapshot(5m, 60);
        Assert.Throws<InvalidOperationException>(() => session.SetPackageSnapshot(100m, 60));
        session.ConfigureBilling(BillingMode.Prepaid, null, 5m, null, new string('A', 64));
        session.Start(Now);
        session.AddPrepaidCharge(2.5m);
        session.Extend(30, Now);
        Assert.Equal(5m, session.InitialPrice);
        Assert.Equal(7.5m, session.PrepaidCharged);
        Assert.Equal(5m, session.PackagePriceSnapshot);
        Assert.Throws<InvalidOperationException>(() => session.SetPackageSnapshot(1m, 1));
    }

    [Fact]
    public void AuthorizationTransfer_PreservesIdPlayerAndOriginalTtl()
    {
        var auth = new PlayerAuthSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddHours(12));
        var id = auth.Id;
        var user = auth.UserId;
        var target = Guid.NewGuid();
        auth.Transfer(target, Now.AddHours(1));
        Assert.Equal(id, auth.Id);
        Assert.Equal(user, auth.UserId);
        Assert.Equal(target, auth.StationId);
        Assert.Equal(Now.AddHours(12), auth.ExpiresAtUtc);
        Assert.Equal(PlayerAuthSessionStatus.Active, auth.Status);
    }

    [Fact]
    public void AuthorizationTransfer_RejectsExpiredEndedAndInvalidTarget()
    {
        var auth = new PlayerAuthSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => auth.Transfer(auth.StationId, Now));
        Assert.Throws<ArgumentException>(() => auth.Transfer(Guid.Empty, Now));
        Assert.Throws<InvalidOperationException>(() => auth.Transfer(Guid.NewGuid(), Now.AddHours(1)));
        auth.End(Now);
        Assert.Throws<InvalidOperationException>(() => auth.Transfer(Guid.NewGuid(), Now));
    }

    [Fact]
    public void Segment_EndIsChronologicalAndImmutable()
    {
        var segment = new StationSessionSegment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => segment.End(Now.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => segment.End(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        segment.End(Now.AddMinutes(2));
        segment.End(Now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => segment.End(Now.AddMinutes(3)));
        Assert.Equal(Now.AddMinutes(2), segment.EndedAtUtc);
    }

    [Fact]
    public void ReservationIncrease_CannotChangeReleasedHoldOrUseFractionalCent()
    {
        var reservation = new WalletReservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m, Now);
        reservation.Increase(0.5m);
        Assert.Equal(1.5m, reservation.Amount);
        Assert.Throws<ArgumentOutOfRangeException>(() => reservation.Increase(0.001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => reservation.Increase(-1m));
        reservation.Release(Now);
        Assert.Throws<InvalidOperationException>(() => reservation.Increase(1m));
    }
}
