using System.Collections.Concurrent;

namespace GameClub.Server.Security.Employees;

public sealed class EmployeeLoginLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private int _operations;

    public bool TryBegin(string sourceIp, DateTime now)
    {
        if (Interlocked.Increment(ref _operations) % 64 == 0)
            foreach (var item in _windows)
            {
                lock (item.Value)
                    if (now >= item.Value.Start + Window && item.Value.InFlight == 0)
                        _windows.TryRemove(new KeyValuePair<string, WindowState>(item.Key, item.Value));
            }
        if (_windows.Count >= 10000 && !_windows.ContainsKey(sourceIp)) return false;
        var state = _windows.GetOrAdd(sourceIp, _ => new WindowState(now));
        lock (state)
        {
            if (now >= state.Start + Window && state.InFlight == 0)
            {
                state.Start = now;
                state.Failures = 0;
                state.Attempts = 0;
            }
            if (state.Failures + state.InFlight >= 5 || state.Attempts >= 20) return false;
            state.InFlight++;
            state.Attempts++;
            return true;
        }
    }

    public void Complete(string sourceIp, bool success)
    {
        if (!_windows.TryGetValue(sourceIp, out var state)) return;
        lock (state)
        {
            state.InFlight = Math.Max(0, state.InFlight - 1);
            if (!success) state.Failures++;
        }
    }

    private sealed class WindowState(DateTime start)
    {
        public DateTime Start = start;
        public int Failures;
        public int Attempts;
        public int InFlight;
    }
}
