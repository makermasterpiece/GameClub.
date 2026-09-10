using GameClub.Agent.Models;

namespace GameClub.Agent.Security;

public interface IStationCredentialStore
{
    Task<StationCredentialData?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(StationCredentialData credential, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}
