using GameClub.Contracts.Client;

namespace GameClub.Agent.Services.Players;

public sealed record AgentPlayerLoginOutcome(
    PlayerLoginResultMessage Result,
    ClientStateMessage? State);

public sealed record AgentPlayerLogoutOutcome(
    PlayerLogoutResultMessage Result,
    ClientStateMessage? State);

public interface IPlayerLoginService
{
    Task<AgentPlayerLoginOutcome> LoginAsync(
        PlayerLoginRequest request,
        CancellationToken cancellationToken);

    Task<AgentPlayerLogoutOutcome> LogoutAsync(
        PlayerLogoutRequest request,
        CancellationToken cancellationToken);

    Task<ClientStateMessage> ReconcileCurrentSessionAsync(CancellationToken cancellationToken);

    Task<ClientStateMessage> ForceLogoutAsync(CancellationToken cancellationToken);
}
