using System.Collections.Concurrent;

namespace GameClub.Server.Services.Players;

public interface IPlayerLoginRateLimiter
{
    bool TryBegin(Guid stationId, DateTime utcNow);
    void RecordFailure(Guid stationId, DateTime utcNow);
    void RecordSuccess(Guid stationId, DateTime utcNow);
}

public sealed class PlayerLoginRateLimiter : IPlayerLoginRateLimiter
{
    public const int MaximumRequestsPerWindow = 10;
    public const int MaximumFailuresPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, WindowState> _states = new();

    public bool TryBegin(Guid stationId, DateTime utcNow)
    {
        var state = _states.GetOrAdd(stationId, _ => new WindowState(utcNow));
        lock (state)
        {
            ResetIfElapsed(state, utcNow);
            if (state.Requests >= MaximumRequestsPerWindow ||
                state.Failures >= MaximumFailuresPerWindow)
            {
                return false;
            }

            state.Requests++;
            return true;
        }
    }

    public void RecordFailure(Guid stationId, DateTime utcNow)
    {
        var state = _states.GetOrAdd(stationId, _ => new WindowState(utcNow));
        lock (state)
        {
            ResetIfElapsed(state, utcNow);
            state.Failures++;
        }
    }

    public void RecordSuccess(Guid stationId, DateTime utcNow)
    {
        if (!_states.TryGetValue(stationId, out var state))
        {
            return;
        }

        lock (state)
        {
            ResetIfElapsed(state, utcNow);
            // The limit is cumulative within the window, not consecutive failures.
        }
    }

    private static void ResetIfElapsed(WindowState state, DateTime utcNow)
    {
        if (utcNow - state.StartedAtUtc < Window)
        {
            return;
        }

        state.StartedAtUtc = utcNow;
        state.Requests = 0;
        state.Failures = 0;
    }

    private sealed class WindowState(DateTime startedAtUtc)
    {
        public DateTime StartedAtUtc { get; set; } = startedAtUtc;
        public int Requests { get; set; }
        public int Failures { get; set; }
    }
}
