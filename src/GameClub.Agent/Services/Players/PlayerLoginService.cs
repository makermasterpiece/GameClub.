using GameClub.Agent.Models;
using GameClub.Agent.Security;
using GameClub.Agent.Services.Client;
using GameClub.Contracts.Client;

namespace GameClub.Agent.Services.Players;

public sealed class PlayerLoginService(
    StationApiClient apiClient,
    IStationCredentialStore credentialStore,
    IClientStateCoordinator clientState,
    ILogger<PlayerLoginService> logger) : IPlayerLoginService
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private static readonly HashSet<string> KnownLoginErrors = new(StringComparer.Ordinal)
    {
        "INVALID_CREDENTIALS",
        "ACCOUNT_DISABLED",
        "ACCOUNT_BANNED",
        "USER_ALREADY_LOGGED_IN",
        "STATION_ALREADY_LOGGED_IN",
        "STATION_UNAVAILABLE",
        "RATE_LIMITED"
    };

    public Task<AgentPlayerLoginOutcome> LoginAsync(
        PlayerLoginRequest request,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(() => LoginCoreAsync(request, cancellationToken), cancellationToken);

    public Task<AgentPlayerLogoutOutcome> LogoutAsync(
        PlayerLogoutRequest request,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(() => LogoutCoreAsync(request, cancellationToken), cancellationToken);

    public Task<ClientStateMessage> ReconcileCurrentSessionAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(() => ReconcileGuardedAsync(cancellationToken), cancellationToken);

    public Task<ClientStateMessage> ForceLogoutAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(() => ForceLogoutCoreAsync(cancellationToken), cancellationToken);

    private async Task<AgentPlayerLoginOutcome> LoginCoreAsync(
        PlayerLoginRequest request,
        CancellationToken cancellationToken)
    {
        var current = await clientState.GetCurrentAsync(cancellationToken);
        if (current.State != ClientShellState.Available)
        {
            return Failure(request.RequestId, "STATION_UNAVAILABLE");
        }

        if (!IsValid(request))
        {
            return Failure(request.RequestId, "INVALID_CREDENTIALS");
        }

        var credential = await credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return Failure(request.RequestId, "SERVER_UNAVAILABLE");
        }

        PlayerApiOperationResult result;
        try
        {
            result = await apiClient.LoginPlayerAsync(
                credential,
                new PlayerLoginApiRequest(request.Username.Trim(), request.Password),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                "Player login server request failed for station {StationId}",
                credential.StationId);
            return Failure(request.RequestId, "SERVER_UNAVAILABLE");
        }

        if (!result.Success || result.Session is null)
        {
            return Failure(request.RequestId, NormalizeError(result.ErrorCode));
        }

        var persisted = ToPersisted(result.Session);
        try
        {
            var state = await clientState.ActivatePlayerSessionAsync(persisted, cancellationToken);
            logger.LogInformation(
                "Player {UserId} session {SessionId} activated locally for station {StationId}",
                persisted.UserId,
                persisted.SessionId,
                credential.StationId);
            return new AgentPlayerLoginOutcome(
                new PlayerLoginResultMessage(
                    request.RequestId,
                    true,
                    null,
                    persisted.SessionId,
                    new ClientPlayerIdentity(
                        persisted.UserId,
                        persisted.Username,
                        persisted.DisplayName)),
                state);
        }
        catch (InvalidOperationException)
        {
            await apiClient.LogoutPlayerAsync(credential, persisted.SessionId, cancellationToken);
            return Failure(request.RequestId, "STATION_UNAVAILABLE");
        }
    }

    private async Task<AgentPlayerLogoutOutcome> LogoutCoreAsync(
        PlayerLogoutRequest request,
        CancellationToken cancellationToken)
    {
        var credential = await credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return LogoutFailure(request.RequestId, "SERVER_UNAVAILABLE");
        }

        try
        {
            var expectedSessionId = await GetExpectedLogoutSessionIdAsync(credential, cancellationToken);
            if (expectedSessionId is { } sessionId)
            {
                var result = await apiClient.LogoutPlayerAsync(credential, sessionId, cancellationToken);
                if (!result.Success)
                {
                    return LogoutFailure(request.RequestId, NormalizeError(result.ErrorCode));
                }
            }

            // A stale identity-bound logout may be a no-op after a transfer/new login.
            // Publish the authoritative occupant, not an unconditional local Available state.
            var state = await ReconcileGuardedAsync(cancellationToken);
            logger.LogInformation(
                "Player logout reconciled for station {StationId}",
                credential.StationId);
            return new AgentPlayerLogoutOutcome(
                new PlayerLogoutResultMessage(request.RequestId, true, null),
                state);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                "Player logout server request failed for station {StationId}",
                credential.StationId);
            if (!cancellationToken.IsCancellationRequested)
                await clientState.MarkServerUnavailableAsync(cancellationToken);
            return LogoutFailure(request.RequestId, "SERVER_UNAVAILABLE");
        }
    }

    private async Task<ClientStateMessage> ReconcileCoreAsync(
        CancellationToken cancellationToken)
    {
        var credential = await credentialStore.LoadAsync(cancellationToken)
            ?? throw new InvalidOperationException("Station credential is unavailable.");
        // Read gaming first: the server may expire it and close the associated auth session.
        var gaming = await apiClient.GetCurrentGamingSessionAsync(credential, cancellationToken);
        var result = await apiClient.GetCurrentPlayerSessionAsync(credential, cancellationToken);
        if (!result.Success)
        {
            throw new HttpRequestException("Server rejected player session reconciliation.");
        }

        var session = result.Session is null ? null : ToPersisted(result.Session);
        var state = await clientState.CompleteSessionReconciliationAsync(
            session,
            session is null ? null : gaming.Session,
            cancellationToken);
        logger.LogDebug(
            "Player session reconciliation completed for station {StationId}; active session: {SessionId}",
            credential.StationId,
            session?.SessionId);
        return state;
    }

    private async Task<ClientStateMessage> ReconcileGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReconcileCoreAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await clientState.MarkServerUnavailableAsync(cancellationToken);
            throw;
        }
    }

    private async Task<ClientStateMessage> ForceLogoutCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credential = await credentialStore.LoadAsync(cancellationToken)
                ?? throw new InvalidOperationException("Station credential is unavailable.");
            var expectedSessionId = await GetExpectedLogoutSessionIdAsync(credential, cancellationToken);
            if (expectedSessionId is { } sessionId)
            {
                var result = await apiClient.LogoutPlayerAsync(credential, sessionId, cancellationToken);
                if (!result.Success)
                {
                    throw new HttpRequestException("Server rejected forced player logout.");
                }
            }

            return await ReconcileGuardedAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await clientState.MarkServerUnavailableAsync(cancellationToken);
            throw;
        }
    }

    private async Task<Guid?> GetExpectedLogoutSessionIdAsync(
        StationCredentialData credential, CancellationToken cancellationToken)
    {
        var state = await clientState.GetCurrentAsync(cancellationToken);
        if (state.PlayerAuthSessionId is { } known) return known;

        state = await ReconcileGuardedAsync(cancellationToken);
        if (state.PlayerAuthSessionId is { } reconciled) return reconciled;
        if (state.State is ClientShellState.Locked or ClientShellState.Maintenance)
        {
            // Locked/maintenance projections deliberately hide identities from the Client.
            // A signed operator command may still log out the authenticated current occupant.
            var current = await apiClient.GetCurrentPlayerSessionAsync(credential, cancellationToken);
            if (!current.Success) throw new HttpRequestException("Server rejected player session lookup.");
            return current.Session?.SessionId;
        }

        return null;
    }

    private async Task<T> RunSerializedAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static bool IsValid(PlayerLoginRequest request) =>
        request.RequestId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.Username) &&
        request.Username.Trim().Length is >= 3 and <= 32 &&
        request.Password is { Length: > 0 and <= 128 };

    private static PersistedPlayerSession ToPersisted(PlayerSessionApiResponse response) =>
        new(
            response.SessionId,
            response.User.Id,
            response.User.Username,
            response.User.DisplayName,
            response.CreatedAtUtc,
            response.ExpiresAtUtc);

    private static AgentPlayerLoginOutcome Failure(Guid requestId, string errorCode) =>
        new(
            new PlayerLoginResultMessage(requestId, false, errorCode, null, null),
            null);

    private static AgentPlayerLogoutOutcome LogoutFailure(Guid requestId, string errorCode) =>
        new(new PlayerLogoutResultMessage(requestId, false, errorCode), null);

    private static string NormalizeError(string? errorCode) =>
        errorCode is not null && KnownLoginErrors.Contains(errorCode)
            ? errorCode
            : errorCode == "AGENT_AUTHENTICATION_FAILED"
                ? errorCode
                : "SERVER_ERROR";
}
