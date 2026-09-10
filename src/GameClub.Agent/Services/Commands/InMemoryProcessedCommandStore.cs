using System.Collections.Concurrent;

namespace GameClub.Agent.Services.Commands;

public sealed class InMemoryProcessedCommandStore : IProcessedCommandStore
{
    private const int MaximumEntries = 1000;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<Guid, ProcessedCommandState> _commands = new();

    private readonly ConcurrentDictionary<string, byte> _nonces = new(StringComparer.Ordinal);

    public bool TryBegin(Guid commandId, string nonce)
    {
        Prune();
        if (!_nonces.TryAdd(nonce, 0))
        {
            return false;
        }

        if (_commands.TryAdd(
            commandId,
            new ProcessedCommandState(ProcessedCommandStatus.Received, null, DateTime.UtcNow)))
        {
            return true;
        }

        _nonces.TryRemove(nonce, out _);
        return false;
    }

    public bool TryGet(Guid commandId, out ProcessedCommandState state) =>
        _commands.TryGetValue(commandId, out state!);

    public void Set(Guid commandId, ProcessedCommandStatus status, string? error = null)
    {
        _commands[commandId] = new ProcessedCommandState(status, error, DateTime.UtcNow);
    }

    public void Remove(Guid commandId) => _commands.TryRemove(commandId, out _);

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var item in _commands)
        {
            if (item.Value.UpdatedAtUtc < cutoff)
            {
                _commands.TryRemove(item.Key, out _);
            }
        }

        if (_commands.Count <= MaximumEntries)
        {
            return;
        }

        foreach (var item in _commands
                     .OrderBy(item => item.Value.UpdatedAtUtc)
                     .Take(_commands.Count - MaximumEntries))
        {
            _commands.TryRemove(item.Key, out _);
        }
    }
}
