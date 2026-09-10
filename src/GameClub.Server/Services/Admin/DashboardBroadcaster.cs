using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GameClub.Server.Services.Admin;

public sealed record DashboardChanged(Guid? StationId = null);

public sealed class DashboardBroadcaster(IServiceScopeFactory scopes, IHubContext<AdminHub> hub,
    TimeProvider clock, ILogger<DashboardBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var response = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetAsync(stoppingToken);
                var fingerprint = MeaningfulFingerprint(response);
                if (fingerprint != previous)
                {
                    await hub.Clients.All.SendAsync("DashboardChanged", new DashboardChanged(), stoppingToken);
                    previous = fingerprint;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Dashboard invalidation scan failed ({ErrorType}); retrying", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static string MeaningfulFingerprint(DashboardResponse response)
    {
        // Elapsed/remaining seconds and heartbeat timestamps advance without meaningful status changes.
        var projection = response.Stations.Select(s => new
        {
            s.Id, s.Name, s.StationGroupId, s.GroupName, s.Status, s.AgentOnline, s.ClientConnected,
            s.ClientState, s.AgentVersion, s.CurrentUser, s.TariffName,
            GamingSession = s.GamingSession is { } game ? new
            {
                game.Id, game.UserId, game.StationId, game.Status, game.StartedAtUtc,
                game.ExpectedEndAtUtc, game.TariffId, game.PackageId
            } : null
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(projection))));
    }
}
