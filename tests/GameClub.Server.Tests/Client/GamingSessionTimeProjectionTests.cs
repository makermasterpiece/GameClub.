using GameClub.Contracts.Gaming;
using Xunit;

namespace GameClub.Server.Tests.Client;

public sealed class GamingSessionTimeProjectionTests
{
    [Fact]
    public void Active_InterpolatesFromServerValuesUsingElapsedDurationOnly()
    {
        var display = GamingSessionTimeProjection.FromSnapshot(Snapshot(), TimeSpan.FromSeconds(12));
        Assert.Equal(48, display.RemainingSeconds);
        Assert.Equal(42, display.ElapsedSeconds);
    }

    [Theory]
    [InlineData("Paused")]
    [InlineData("Pending")]
    public void NonActiveStatus_DoesNotConsumeTime(string status)
    {
        var display = GamingSessionTimeProjection.FromSnapshot(
            Snapshot() with { Status = status }, TimeSpan.FromHours(1));
        Assert.Equal(60, display.RemainingSeconds);
        Assert.Equal(30, display.ElapsedSeconds);
    }

    [Fact]
    public void LocalZero_DoesNotChangeAuthoritativeStatus()
    {
        var snapshot = Snapshot();
        var display = GamingSessionTimeProjection.FromSnapshot(snapshot, TimeSpan.FromMinutes(2));
        Assert.Equal(0, display.RemainingSeconds);
        Assert.Equal("Active", snapshot.Status);
    }

    [Fact]
    public void WallClockJump_DoesNotAffectMonotonicProjection()
    {
        var snapshot = Snapshot();
        var before = GamingSessionTimeProjection.FromSnapshot(snapshot, TimeSpan.FromSeconds(10));
        var changedWallClock = snapshot with
        {
            ServerTimeUtc = snapshot.ServerTimeUtc.AddDays(-100),
            ExpectedEndAtUtc = snapshot.ExpectedEndAtUtc?.AddDays(-100)
        };
        var after = GamingSessionTimeProjection.FromSnapshot(changedWallClock, TimeSpan.FromSeconds(10));
        Assert.Equal(before, after);
    }

    [Fact]
    public void NewSnapshot_ResynchronizesAndNullRemainingStaysUnlimited()
    {
        var updated = Snapshot() with { RemainingSeconds = null, ElapsedSeconds = 100 };
        var display = GamingSessionTimeProjection.FromSnapshot(updated, TimeSpan.FromSeconds(5));
        Assert.Null(display.RemainingSeconds);
        Assert.Equal(105, display.ElapsedSeconds);
    }

    private static GamingSessionSnapshot Snapshot() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Active", DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1), 60, DateTime.UtcNow, 30);
}
