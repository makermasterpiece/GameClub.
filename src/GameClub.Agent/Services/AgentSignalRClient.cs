using GameClub.Agent.Configuration;
using GameClub.Agent.Models;
using GameClub.Agent.Services.Commands;
using GameClub.Agent.Security;
using GameClub.Agent.Services.Players;
using GameClub.Domain.Commands;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Services;

public sealed class AgentSignalRClient(
    IOptions<ServerOptions> serverOptions,
    IOptions<SecurityOptions> securityOptions,
    IHostEnvironment environment,
    StationApiClient apiClient,
    IAgentCommandHandler commandHandler,
    StationSessionSyncSignal sessionSyncSignal,
    IHostApplicationLifetime applicationLifetime,
    ILogger<AgentSignalRClient> logger) : IAgentCommandReporter, IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Uri _hubUrl = BuildHubUrl(
        ServerEndpointPolicy.Validate(serverOptions.Value, securityOptions.Value, environment));
    private HubConnection? _connection;
    private Guid? _stationId;

    public async Task EnsureConnectedAsync(
        StationCredentialData credential,
        CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_stationId != credential.StationId)
            {
                await DisposeConnectionAsync();
            }

            _connection ??= BuildConnection(credential);

            if (_connection.State is HubConnectionState.Connected or
                HubConnectionState.Connecting or
                HubConnectionState.Reconnecting)
            {
                return;
            }

            try
            {
                await _connection.StartAsync(cancellationToken);
                logger.LogInformation(
                    "SignalR connected for station {StationId}",
                    credential.StationId);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "SignalR connection failed for station {StationId}: {ErrorMessage}",
                    credential.StationId,
                    exception.Message);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await DisposeConnectionAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public Task AcknowledgeAsync(Guid commandId, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync("AcknowledgeCommand", commandId, cancellationToken);

    public Task CompleteAsync(Guid commandId, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync("CompleteCommand", commandId, cancellationToken);

    public Task FailAsync(Guid commandId, string error, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync("FailCommand", commandId, error, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            await DisposeConnectionAsync();
        }
        finally
        {
            _connectionLock.Release();
            _connectionLock.Dispose();
        }
    }

    private HubConnection BuildConnection(StationCredentialData credential)
    {
        _stationId = credential.StationId;

        var connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl, options =>
            {
                options.AccessTokenProvider = async () =>
                    (await apiClient.GetSessionTokenAsync(
                        credential,
                        applicationLifetime.ApplicationStopping)).AccessToken;
            })
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

        connection.On<SignedAgentCommandEnvelope>(
            "ExecuteCommand",
            command => ProcessCommandAsync(command, applicationLifetime.ApplicationStopping));

        connection.On<Guid>("GamingSessionChanged", stationId =>
        {
            if (stationId == credential.StationId)
            {
                sessionSyncSignal.Notify();
            }
        });

        connection.Reconnecting += exception =>
        {
            logger.LogWarning(
                "SignalR reconnecting for station {StationId}: {ErrorMessage}",
                credential.StationId,
                exception?.Message);
            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            sessionSyncSignal.Notify();
            logger.LogInformation(
                "SignalR reconnected for station {StationId}. Connection: {ConnectionId}",
                credential.StationId,
                connectionId);
            return Task.CompletedTask;
        };

        connection.Closed += exception =>
        {
            logger.LogWarning(
                "SignalR disconnected for station {StationId}: {ErrorMessage}. The heartbeat loop will reconnect it",
                credential.StationId,
                exception?.Message);
            return Task.CompletedTask;
        };

        return connection;
    }

    private async Task ProcessCommandAsync(
        SignedAgentCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        try
        {
            await commandHandler.HandleAsync(command, this, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected error while processing command {CommandId}",
                command.CommandId);
        }
    }

    private HubConnection GetConnectedConnection()
    {
        if (_connection is not { State: HubConnectionState.Connected } connection)
        {
            throw new InvalidOperationException("SignalR is not connected.");
        }

        return connection;
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.StopAsync();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Error while stopping SignalR connection");
        }

        await _connection.DisposeAsync();
        _connection = null;
        _stationId = null;
    }

    private static Uri BuildHubUrl(Uri baseAddress)
    {
        var normalizedBaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + "/");
        return new Uri(normalizedBaseAddress, "hubs/stations");
    }
}
