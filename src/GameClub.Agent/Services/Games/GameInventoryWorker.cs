using GameClub.Agent.Security;
using GameClub.Contracts.Games;

namespace GameClub.Agent.Services.Games;

public sealed class GameInventoryWorker(ILocalPlayniteLibrary library, IStationCredentialStore credentials,
    StationApiClient api, ILogger<GameInventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var credential = await credentials.LoadAsync(stoppingToken);
                if (credential is not null)
                    await api.ReportGameInventoryAsync(credential,
                        new StationGameInventoryRequest(await library.ReadInventoryAsync(stoppingToken)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Game inventory refresh failed ({ErrorType}); retrying", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
