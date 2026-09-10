using GameClub.Domain.Commands;
using GameClub.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GameClub.Server.Services.Commands;

public sealed class SignalRStationCommandTransport(
    IHubContext<StationsHub> hubContext,
    StationConnectionRegistry connectionRegistry) : IStationCommandTransport
{
    public bool IsConnected(Guid stationId) => connectionRegistry.IsConnected(stationId);

    public Task SendAsync(
        Guid stationId,
        SignedAgentCommandEnvelope command,
        CancellationToken cancellationToken) =>
        hubContext.Clients
            .Group(StationsHub.GetStationGroupName(stationId))
            .SendAsync("ExecuteCommand", command, cancellationToken);
}
