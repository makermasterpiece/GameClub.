using System.Reflection;
using GameClub.Agent.Configuration;
using GameClub.Agent.Models;
using GameClub.Agent.Security;
using GameClub.Agent.Services;
using GameClub.Domain.Security;
using GameClub.Agent.Services.Client;
using GameClub.Agent.Services.Players;
using Microsoft.Extensions.Options;

namespace GameClub.Agent;

public sealed class Worker(
    StationApiClient apiClient,
    AgentSignalRClient signalRClient,
    IPlayerLoginService playerLoginService,
    IClientStateCoordinator clientState,
    IClientStateNotifier clientNotifier,
    StationSessionSyncSignal sessionSyncSignal,
    IStationCredentialStore credentialStore,
    IOptions<StationOptions> stationOptions,
    IOptions<SecurityOptions> securityOptions,
    TimeProvider clock,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly string _machineName = Environment.MachineName;
    private readonly string _agentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    private readonly string _stationName = GetStationName(stationOptions.Value);
    private readonly SecurityOptions _security = securityOptions.Value;
    private volatile bool _heartbeatAccepted;
    private long _reachabilityRevision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var credential = await credentialStore.LoadAsync(stoppingToken);
        var synchronizationTask = Task.CompletedTask;
        logger.LogInformation(
            "GameClub Agent {AgentVersion} started on {MachineName} as {StationName}",
            _agentVersion,
            _machineName,
            _stationName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (credential is null)
                {
                    credential = await EnrollAsync(stoppingToken);
                    await credentialStore.SaveAsync(credential, stoppingToken);
                }

                var projection = await clientState.GetCurrentAsync(stoppingToken);
                var lastClientSeen = clientState.ClientLastSeenAtUtc;
                var now = clock.GetUtcNow().UtcDateTime;
                var connected = clientState.ClientConnected && lastClientSeen is { } seen &&
                                seen.Kind == DateTimeKind.Utc && seen <= now && now - seen <= TimeSpan.FromSeconds(15);
                var heartbeatResult = await apiClient.SendHeartbeatAsync(
                    credential,
                    new HeartbeatRequest(_machineName, _agentVersion, connected,
                        connected ? projection.State.ToString() : "Offline", lastClientSeen),
                    stoppingToken);

                if (heartbeatResult == HeartbeatResult.CredentialRejected)
                {
                    MarkHeartbeatUnavailable();
                    logger.LogError(
                        "Server rejected credential for station {StationId}. " +
                        "A new enrollment token and local credential reset are required",
                        credential.StationId);
                    await signalRClient.ResetAsync(stoppingToken);
                    await PublishServerUnavailableAsync(stoppingToken);
                }
                else
                {
                    _heartbeatAccepted = true;
                    logger.LogDebug("Heartbeat sent for station {StationId}", credential.StationId);
                    // Session HTTP requests and SignalR reconnects must not delay independent heartbeats.
                    // Keep only one reconciliation in flight; no unbounded queue or overlapping state writes.
                    if (synchronizationTask.IsCompleted)
                        synchronizationTask = SynchronizeSessionAsync(credential, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                MarkHeartbeatUnavailable();
                await PublishServerUnavailableAsync(stoppingToken);
                logger.LogWarning(
                    "Server is unavailable ({ErrorMessage}); retrying in {RetrySeconds} seconds",
                    exception.Message,
                    HeartbeatInterval.TotalSeconds);
            }
            catch (TaskCanceledException exception)
            {
                MarkHeartbeatUnavailable();
                await PublishServerUnavailableAsync(stoppingToken);
                logger.LogWarning(
                    "Server request timed out ({ErrorMessage}); retrying in {RetrySeconds} seconds",
                    exception.Message,
                    HeartbeatInterval.TotalSeconds);
            }
            catch (Exception exception)
            {
                MarkHeartbeatUnavailable();
                await PublishServerUnavailableAsync(stoppingToken);
                logger.LogError(
                    exception,
                    "Agent cycle failed; retrying in {RetrySeconds} seconds",
                    HeartbeatInterval.TotalSeconds);
            }

            try
            {
                await sessionSyncSignal.WaitAsync(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("GameClub Agent is stopping");
        await synchronizationTask;
        await signalRClient.ResetAsync(CancellationToken.None);
    }

    private void MarkHeartbeatUnavailable()
    {
        _heartbeatAccepted = false;
        Interlocked.Increment(ref _reachabilityRevision);
    }

    private async Task SynchronizeSessionAsync(StationCredentialData credential, CancellationToken ct)
    {
        var revision = Interlocked.Read(ref _reachabilityRevision);
        try
        {
            var reconciledState = await playerLoginService.ReconcileCurrentSessionAsync(ct);
            if (!_heartbeatAccepted || revision != Interlocked.Read(ref _reachabilityRevision))
            {
                await PublishServerUnavailableAsync(ct);
                return;
            }
            await clientNotifier.PublishAsync(reconciledState, waitForAcknowledgement: false, ct);
            if (_heartbeatAccepted && revision == Interlocked.Read(ref _reachabilityRevision))
                await signalRClient.EnsureConnectedAsync(credential, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await PublishServerUnavailableAsync(ct);
            logger.LogWarning("Station session synchronization failed ({ErrorType}); heartbeats continue independently", exception.GetType().Name);
        }
    }

    private async Task PublishServerUnavailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await clientState.MarkServerUnavailableAsync(cancellationToken);
            await clientNotifier.PublishAsync(state, waitForAcknowledgement: false, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Unable to publish offline state ({ErrorType})", exception.GetType().Name);
        }
    }

    private async Task<StationCredentialData> EnrollAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_security.EnrollmentToken))
        {
            throw new InvalidOperationException(
                "No protected station credential exists. Set Security__EnrollmentToken for one-time enrollment.");
        }

        logger.LogInformation(
            "Enrolling station {StationName} on machine {MachineName}",
            _stationName,
            _machineName);
        var response = await apiClient.EnrollAsync(
            new EnrollStationRequest(
                _security.EnrollmentToken,
                _stationName,
                _machineName,
                _agentVersion),
            cancellationToken);
        if (response.StationId == Guid.Empty ||
            !string.Equals(
                response.SignatureAlgorithm,
                CommandEnvelopeCryptography.SignatureAlgorithm,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Server returned an invalid station identity.");
        }

        logger.LogInformation("Station enrolled with id {StationId}", response.StationId);
        return new StationCredentialData(
            response.StationId,
            response.StationSecret,
            response.ServerPublicKey,
            response.SignatureAlgorithm);
    }

    private static string GetStationName(StationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            throw new InvalidOperationException("Station:Name must be configured.");
        }

        return options.Name.Trim();
    }
}
