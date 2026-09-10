namespace GameClub.Contracts.Gaming;

// Display-only interpolation. The caller supplies monotonic elapsed time, never the wall clock.
// Reaching zero does not end or authorize a session; only the server changes its status.
public sealed record GamingSessionTimeProjection(int? RemainingSeconds, long ElapsedSeconds)
{
    public static GamingSessionTimeProjection FromSnapshot(
        GamingSessionSnapshot snapshot,
        TimeSpan monotonicElapsed)
    {
        var running = string.Equals(snapshot.Status, "Active", StringComparison.Ordinal);
        var delta = running ? Math.Max(0L, (long)monotonicElapsed.TotalSeconds) : 0L;
        var initialElapsed = Math.Max(0L, snapshot.ElapsedSeconds);
        var elapsed = initialElapsed > long.MaxValue - delta ? long.MaxValue : initialElapsed + delta;
        var remaining = snapshot.RemainingSeconds is { } initialRemaining
            ? (int?)Math.Max(0L, (long)initialRemaining - delta)
            : null;
        return new GamingSessionTimeProjection(remaining, elapsed);
    }
}
