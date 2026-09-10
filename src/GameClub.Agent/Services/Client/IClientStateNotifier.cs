using GameClub.Contracts.Client;

namespace GameClub.Agent.Services.Client;

public sealed record ClientStateDelivery(bool ClientConnected, bool Acknowledged);

public interface IClientStateNotifier
{
    Task<ClientStateDelivery> PublishAsync(
        ClientStateMessage state,
        bool waitForAcknowledgement,
        CancellationToken cancellationToken);
}
