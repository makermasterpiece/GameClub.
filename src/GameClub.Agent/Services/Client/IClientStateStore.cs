using GameClub.Contracts.Client;

namespace GameClub.Agent.Services.Client;

public sealed record PersistedClientState(ClientShellState State, DateTime UpdatedAtUtc);

public interface IClientStateStore
{
    Task<PersistedClientState?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ClientShellState state, DateTime updatedAtUtc, CancellationToken cancellationToken);
}
