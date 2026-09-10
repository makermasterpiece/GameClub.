using System.Collections.Concurrent;

namespace GameClub.Server.Security;

public interface IReplayProtectionStore
{
    bool TryUse(Guid stationId, string nonce, DateTime expiresAtUtc);
}

public sealed class InMemoryReplayProtectionStore(TimeProvider timeProvider) : IReplayProtectionStore
{
    private readonly ConcurrentDictionary<string, DateTime> _nonces = new(StringComparer.Ordinal);

    public bool TryUse(Guid stationId, string nonce, DateTime expiresAtUtc)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var item in _nonces)
        {
            if (item.Value <= now)
            {
                _nonces.TryRemove(item.Key, out _);
            }
        }

        return _nonces.TryAdd($"{stationId:D}:{nonce}", expiresAtUtc);
    }
}
