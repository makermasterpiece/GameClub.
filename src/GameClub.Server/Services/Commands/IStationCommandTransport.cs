using GameClub.Domain.Commands;

namespace GameClub.Server.Services.Commands;

public interface IStationCommandTransport
{
    bool IsConnected(Guid stationId);

    Task SendAsync(
        Guid stationId,
        SignedAgentCommandEnvelope command,
        CancellationToken cancellationToken);
}
