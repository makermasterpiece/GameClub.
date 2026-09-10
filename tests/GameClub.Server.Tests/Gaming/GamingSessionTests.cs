using GameClub.Domain.Gaming;
using Xunit;

namespace GameClub.Server.Tests.Gaming;

public sealed class GamingSessionTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewSession_IsCreatedWithoutStartingTheClock()
    {
        var session = Create(60);

        Assert.Equal(GamingSessionStatus.Created, session.Status);
        Assert.Equal(Now, session.CreatedAtUtc);
        Assert.Null(session.StartedAtUtc);
        Assert.Null(session.ExpectedEndAtUtc);
        Assert.Null(session.EndedAtUtc);
        Assert.Equal(3600, session.RemainingSeconds(Now.AddHours(4)));
        Assert.Equal(0, session.ElapsedSeconds(Now.AddHours(4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10081)]
    public void InvalidPurchasedMinutes_AreRejected(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(minutes));

    [Fact]
    public void EmptyIdentifiers_AreRejected()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new GamingSession(Guid.Empty, id, id, 60, Now));
        Assert.Throws<ArgumentException>(() => new GamingSession(id, Guid.Empty, id, 60, Now));
        Assert.Throws<ArgumentException>(() => new GamingSession(id, id, Guid.Empty, 60, Now));
    }

    [Fact]
    public void NonUtcTimestamp_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new GamingSession(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 60, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        var session = Create();
        Assert.Throws<ArgumentException>(() => session.Start(DateTime.SpecifyKind(Now, DateTimeKind.Local)));
    }

    [Fact]
    public void Start_UsesActualStartRatherThanCreationTime()
    {
        var session = Create(60);
        session.Start(Now.AddMinutes(10));

        Assert.Equal(GamingSessionStatus.Active, session.Status);
        Assert.Equal(Now.AddMinutes(10), session.StartedAtUtc);
        Assert.Equal(Now.AddMinutes(70), session.ExpectedEndAtUtc);
        Assert.Equal(Now.AddMinutes(10), session.LastResumedAtUtc);
    }

    [Fact]
    public void ActiveTimer_IsCalculatedByPassedServerTime()
    {
        var session = Started(60);

        Assert.Equal(45, session.ElapsedSeconds(Now.AddSeconds(45)));
        Assert.Equal(3555, session.RemainingSeconds(Now.AddSeconds(45)));
    }

    [Fact]
    public void RemainingSeconds_RoundUpToAvoidPrematureExpiration()
    {
        var session = Started(1);

        Assert.Equal(1, session.RemainingSeconds(Now.AddSeconds(59.9)));
        Assert.Equal(59, session.ElapsedSeconds(Now.AddSeconds(59.9)));
        Assert.False(session.IsExpiredAt(Now.AddSeconds(59.9)));
        Assert.True(session.IsExpiredAt(Now.AddMinutes(1)));
    }

    [Fact]
    public void Pause_FreezesClockAndRemovesDeadline()
    {
        var session = Started(60);
        session.Pause(Now.AddMinutes(10));

        Assert.Equal(GamingSessionStatus.Paused, session.Status);
        Assert.Null(session.ExpectedEndAtUtc);
        Assert.Null(session.LastResumedAtUtc);
        Assert.Equal(600, session.AccumulatedSeconds);
        Assert.Equal(3000, session.RemainingSeconds(Now.AddDays(1)));
        Assert.Equal(600, session.ElapsedSeconds(Now.AddDays(1)));
        Assert.False(session.IsExpiredAt(Now.AddDays(1)));
    }

    [Fact]
    public void Resume_ExcludesPausedDurationFromDeadline()
    {
        var session = Started(60);
        session.Pause(Now.AddMinutes(10));
        session.Resume(Now.AddMinutes(25));

        Assert.Equal(Now.AddMinutes(75), session.ExpectedEndAtUtc);
        Assert.Equal(900, session.ElapsedSeconds(Now.AddMinutes(30)));
        Assert.Equal(2700, session.RemainingSeconds(Now.AddMinutes(30)));
    }

    [Fact]
    public void RepeatedSubsecondPauses_DoNotCreateFreeTime()
    {
        var session = Started(1);
        session.Pause(Now.AddMilliseconds(500));
        session.Resume(Now.AddSeconds(1));
        session.Pause(Now.AddMilliseconds(1500));
        session.Resume(Now.AddSeconds(2));

        Assert.Equal(TimeSpan.TicksPerSecond, session.AccumulatedTicks);
        Assert.Equal(1, session.AccumulatedSeconds);
        Assert.Equal(Now.AddSeconds(61), session.ExpectedEndAtUtc);
        Assert.Equal(0, session.RemainingSeconds(Now.AddSeconds(61)));
    }

    [Fact]
    public void ExpiredClock_IsCappedAtPurchasedDuration()
    {
        var session = Started(1);

        Assert.True(session.IsExpiredAt(Now.AddHours(1)));
        Assert.Equal(0, session.RemainingSeconds(Now.AddHours(1)));
        Assert.Equal(60, session.ElapsedSeconds(Now.AddHours(1)));
    }

    [Fact]
    public void EndingExpiredSession_UsesDeadlineEvenIfSweepWasDelayed()
    {
        var session = Started(1);

        Assert.True(session.End(Now.AddHours(1)));
        Assert.Equal(GamingSessionStatus.Completed, session.Status);
        Assert.Equal(Now.AddMinutes(1), session.EndedAtUtc);
        Assert.Equal(60, session.AccumulatedSeconds);
        Assert.Equal(60, session.ElapsedSeconds(Now.AddHours(2)));
        Assert.Equal(0, session.RemainingSeconds(Now.AddHours(2)));
    }

    [Fact]
    public void ManualEnd_StopsClockEarly()
    {
        var session = Started(60);
        session.End(Now.AddMinutes(12));

        Assert.Equal(Now.AddMinutes(12), session.EndedAtUtc);
        Assert.Equal(720, session.ElapsedSeconds(Now.AddDays(1)));
        Assert.Equal(0, session.RemainingSeconds(Now.AddDays(1)));
    }

    [Fact]
    public void EndingPausedSession_DoesNotChargePausedTime()
    {
        var session = Started(60);
        session.Pause(Now.AddMinutes(12));
        session.End(Now.AddMinutes(25));

        Assert.Equal(Now.AddMinutes(25), session.EndedAtUtc);
        Assert.Equal(720, session.ElapsedSeconds(Now.AddDays(1)));
    }

    [Fact]
    public void End_IsIdempotent()
    {
        var session = Started(60);

        Assert.True(session.End(Now.AddMinutes(12)));
        Assert.False(session.End(Now.AddMinutes(30)));
        Assert.Equal(Now.AddMinutes(12), session.EndedAtUtc);
        Assert.Equal(720, session.AccumulatedSeconds);
    }

    [Fact]
    public void EndBeforeStart_CancelsWithoutElapsedTime()
    {
        var session = Create(60);

        Assert.True(session.End(Now.AddMinutes(5)));
        Assert.Equal(GamingSessionStatus.Cancelled, session.Status);
        Assert.Equal(0, session.ElapsedSeconds(Now.AddMinutes(5)));
        Assert.False(session.End(Now.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => session.Start(Now.AddMinutes(6)));
    }

    [Fact]
    public void UnlimitedSession_HasNoDeadlineAndStillTracksElapsedTime()
    {
        var session = Started(null);
        session.Pause(Now.AddMinutes(10));
        session.Resume(Now.AddMinutes(25));

        Assert.Null(session.ExpectedEndAtUtc);
        Assert.Null(session.RemainingSeconds(Now.AddHours(10)));
        Assert.False(session.IsExpiredAt(Now.AddYears(1)));
        Assert.Equal(900, session.ElapsedSeconds(Now.AddMinutes(30)));
        session.End(Now.AddMinutes(30));
        Assert.Equal(900, session.AccumulatedSeconds);
    }

    [Fact]
    public void InvalidStatusTransitions_AreRejected()
    {
        var created = Create();
        Assert.Throws<InvalidOperationException>(() => created.Pause(Now));
        Assert.Throws<InvalidOperationException>(() => created.Resume(Now));
        var active = Started();
        Assert.Throws<InvalidOperationException>(() => active.Start(Now));
        Assert.Throws<InvalidOperationException>(() => active.Resume(Now));
        active.Pause(Now);
        Assert.Throws<InvalidOperationException>(() => active.Pause(Now));
        active.End(Now);
        Assert.Throws<InvalidOperationException>(() => active.Resume(Now));
    }

    [Fact]
    public void PauseAtDeadline_IsRejectedWithoutRevivingExpiredSession()
    {
        var session = Started(1);

        Assert.Throws<InvalidOperationException>(() => session.Pause(Now.AddMinutes(1)));
        Assert.True(session.IsExpiredAt(Now.AddMinutes(1)));
        Assert.Equal(GamingSessionStatus.Active, session.Status);
    }

    [Fact]
    public void BackwardsTransitionTime_IsRejected()
    {
        var session = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Start(Now.AddTicks(-1)));
        session.Start(Now);
        session.Pause(Now.AddMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Resume(Now.AddSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.End(Now));
    }

    [Fact]
    public void ExtendActiveSession_PreservesClockAndAddsBudget()
    {
        var session = Started(60);
        session.Extend(30, Now.AddMinutes(10));

        Assert.Equal(30, session.AddedMinutes);
        Assert.Equal(60, session.PurchasedMinutes);
        Assert.Equal(Now.AddMinutes(90), session.ExpectedEndAtUtc);
        Assert.Equal(600, session.ElapsedSeconds(Now.AddMinutes(10)));
        Assert.Equal(4800, session.RemainingSeconds(Now.AddMinutes(10)));
    }

    [Fact]
    public void ExtendPausedSession_AppliesBudgetOnResume()
    {
        var session = Started(60);
        session.Pause(Now.AddMinutes(10));
        session.Extend(30, Now.AddMinutes(20));

        Assert.Null(session.ExpectedEndAtUtc);
        Assert.Equal(4800, session.RemainingSeconds(Now.AddMinutes(20)));
        session.Resume(Now.AddMinutes(30));
        Assert.Equal(Now.AddMinutes(110), session.ExpectedEndAtUtc);
    }

    [Fact]
    public void InvalidExtensions_AreRejectedWithoutMutation()
    {
        var session = Started(60);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Extend(0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Extend(int.MaxValue, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Extend(10080, Now));
        Assert.Equal(0, session.AddedMinutes);
        Assert.Equal(Now.AddMinutes(60), session.ExpectedEndAtUtc);
        Assert.Throws<InvalidOperationException>(() => Started(null).Extend(1, Now));
        Assert.Throws<InvalidOperationException>(() => session.Extend(1, Now.AddMinutes(60)));
    }

    [Fact]
    public void Transfer_ChangesStationWithoutResettingClockOrSessionIdentity()
    {
        var session = Started(60);
        var id = session.Id;
        var userId = session.UserId;
        var newStationId = Guid.NewGuid();
        session.Transfer(newStationId, Now.AddMinutes(10));

        Assert.Equal(newStationId, session.StationId);
        Assert.Equal(id, session.Id);
        Assert.Equal(userId, session.UserId);
        Assert.Equal(Now.AddMinutes(60), session.ExpectedEndAtUtc);
        Assert.Equal(600, session.ElapsedSeconds(Now.AddMinutes(10)));
    }

    [Fact]
    public void InvalidTransfer_IsRejected()
    {
        var session = Started(60);
        Assert.Throws<ArgumentException>(() => session.Transfer(Guid.Empty, Now));
        Assert.Throws<ArgumentException>(() => session.Transfer(session.StationId, Now));
        session.End(Now);
        Assert.Throws<InvalidOperationException>(() => session.Transfer(Guid.NewGuid(), Now));
    }

    [Fact]
    public void InitialPricing_IsOptionalAndSetOnlyBeforeStart()
    {
        var session = Create();
        var tariffId = Guid.NewGuid();
        Assert.Null(session.InitialPrice);
        session.SetPricing(tariffId, null, 12.34m);
        Assert.Equal(tariffId, session.TariffId);
        Assert.Equal(12.34m, session.InitialPrice);
        session.Start(Now);
        Assert.Throws<InvalidOperationException>(() => session.SetPricing(tariffId, null, 50m));
    }

    [Fact]
    public void FinalPrice_IsNonnegativeAndImmutableOnceFinalized()
    {
        var session = Started();
        Assert.Throws<InvalidOperationException>(() => session.SetFinalPrice(10m));
        session.End(Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFinalPrice(-1m));
        session.SetFinalPrice(12.34m);
        session.SetFinalPrice(12.34m);
        Assert.Equal(12.34m, session.FinalPrice);
        Assert.Throws<InvalidOperationException>(() => session.SetFinalPrice(50m));
    }

    [Fact]
    public void SessionEvent_PreservesImmutableAuditData()
    {
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var sessionEvent = new SessionEvent(id, sessionId, SessionEventType.SessionPaused, Now, "Operator pause", employeeId);

        Assert.Equal(id, sessionEvent.Id);
        Assert.Equal(sessionId, sessionEvent.GamingSessionId);
        Assert.Equal(SessionEventType.SessionPaused, sessionEvent.Type);
        Assert.Equal(Now, sessionEvent.CreatedAtUtc);
        Assert.Equal("Operator pause", sessionEvent.Details);
        Assert.Equal(employeeId, sessionEvent.EmployeeId);
        Assert.All(typeof(SessionEvent).GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
    }

    [Fact]
    public void SessionEvent_RejectsInvalidIdentityTypeAndOversizedDetails()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new SessionEvent(Guid.Empty, id, SessionEventType.SessionEnded, Now));
        Assert.Throws<ArgumentException>(() => new SessionEvent(id, Guid.Empty, SessionEventType.SessionEnded, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionEvent(id, id, (SessionEventType)99, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionEvent(id, id, SessionEventType.SessionEnded, Now, new string('x', 2001)));
        Assert.Throws<ArgumentException>(() => new SessionEvent(id, id, SessionEventType.SessionEnded, DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
    }

    private static GamingSession Create(int? purchasedMinutes = 60) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), purchasedMinutes, Now);

    private static GamingSession Started(int? purchasedMinutes = 60)
    {
        var session = Create(purchasedMinutes);
        session.Start(Now);
        return session;
    }
}
