using GameClub.Application.Abstractions;
using GameClub.Server.Hubs;
using GameClub.Server.Services.Admin;
using Microsoft.AspNetCore.SignalR;

namespace GameClub.Server.Services;

public sealed class ClubEvents(IHubContext<StationsHub> hub, IHubContext<AdminHub> adminHub, ILogger<ClubEvents> logger) : IClubEvents
{
    public async Task StationChangedAsync(Guid stationId, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(StationsHub.GetStationGroupName(stationId))
                .SendAsync("GamingSessionChanged", stationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Durable DB state is already committed; polling reconciles missed invalidations.
            logger.LogWarning("Station notification failed for {StationId} ({ErrorType})", stationId, ex.GetType().Name);
        }

        try
        {
            await adminHub.Clients.All.SendAsync("DashboardChanged", new DashboardChanged(stationId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Dashboard notification failed for {StationId} ({ErrorType})", stationId, ex.GetType().Name);
        }
    }
}
