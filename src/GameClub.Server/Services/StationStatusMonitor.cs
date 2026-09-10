using GameClub.Domain.Stations;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Stations;
using GameClub.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed class StationStatusMonitor(
    IServiceScopeFactory scopeFactory,
    IHubContext<StationsHub> hubContext,
    ILogger<StationStatusMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await MarkStaleStationsOffline(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to check station heartbeat timeouts");
            }
        }
    }

    private async Task MarkStaleStationsOffline(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GameClubDbContext>();
        var now = DateTime.UtcNow;
        var cutoff = now - HeartbeatTimeout;

        var staleStations = await dbContext.Stations
            .AsNoTracking()
            .Where(station =>
                station.Status == StationStatus.Online &&
                station.LastSeenAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var station in staleStations)
        {
            var updatedRows = await dbContext.Stations
                .Where(candidate =>
                    candidate.Id == station.Id &&
                    candidate.Status == StationStatus.Online &&
                    candidate.LastSeenAtUtc < cutoff)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        candidate => candidate.Status,
                        StationStatus.Offline),
                    cancellationToken);

            if (updatedRows == 0)
            {
                continue;
            }

            logger.LogInformation(
                "Station {StationId} ({MachineName}) is offline",
                station.Id,
                station.MachineName);

            await hubContext.Clients.All.SendAsync(
                "StationStatusChanged",
                new StationStatusChangedMessage(
                    station.Id,
                    station.Name,
                    StationStatus.Offline,
                    station.LastSeenAtUtc),
                cancellationToken);
        }
    }
}
