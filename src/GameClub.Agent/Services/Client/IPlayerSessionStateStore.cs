namespace GameClub.Agent.Services.Client;

public sealed record PersistedPlayerSession(
    Guid SessionId,
    Guid UserId,
    string Username,
    string? DisplayName,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public interface IPlayerSessionStateStore
{
    Task<PersistedPlayerSession?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PersistedPlayerSession session, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
