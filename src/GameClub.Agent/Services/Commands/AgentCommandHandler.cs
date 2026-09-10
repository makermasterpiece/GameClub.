using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameClub.Agent.Models;
using GameClub.Agent.Security;
using GameClub.Agent.Services.Client;
using GameClub.Contracts.Client;
using GameClub.Domain.Commands;
using GameClub.Domain.Security;
using GameClub.Agent.Services.Players;
using GameClub.Agent.Services.Games;
using GameClub.Contracts.Games;
using AgentCommandType = GameClub.Agent.Models.AgentCommandType;

namespace GameClub.Agent.Services.Commands;

public sealed class AgentCommandHandler(
    IProcessedCommandStore processedCommands,
    IStationCredentialStore credentialStore,
    IServerCommandSignatureVerifier signatureVerifier,
    IClientStateCoordinator clientState,
    IClientStateNotifier clientNotifier,
    IPlayerLoginService playerLoginService,
    RestartStationCommandHandler restartStation,
    ShutdownStationCommandHandler shutdownStation,
    TimeProvider timeProvider,
    ILogger<AgentCommandHandler> logger,
    IAgentGameLaunchService? games = null,
    IPlayniteActionTransport? playnite = null) : IAgentCommandHandler
{
    private const int MaximumErrorLength = 2000;
    private static readonly TimeSpan AllowedFutureClockSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumCommandAge = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task HandleAsync(
        SignedAgentCommandEnvelope command,
        IAgentCommandReporter reporter,
        CancellationToken cancellationToken)
    {
        var credential = await credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            logger.LogWarning("SecurityWarning: command rejected because no station credential is available");
            return;
        }

        if (!ValidateEnvelopeStructure(command, credential, out var commandType, out var rejection))
        {
            logger.LogWarning(
                "SecurityWarning: command {CommandId} rejected. Reason: {Reason}",
                command.CommandId,
                rejection);
            return;
        }

        if (!signatureVerifier.Verify(command, credential))
        {
            logger.LogWarning("SecurityWarning: invalid signature for command {CommandId}", command.CommandId);
            return;
        }

        if (!TryValidatePayload(command, commandType, out var payloadError))
        {
            logger.LogWarning(
                "SecurityWarning: command {CommandId} payload rejected. Reason: {Reason}",
                command.CommandId,
                payloadError);
            return;
        }

        var powerCommand = IsPowerCommand(commandType);
        if (powerCommand && processedCommands.TryGet(command.CommandId, out _))
        {
            // An expired retransmission must not report Failed after an accepted OS request and release its lease.
            await ReportDuplicateAsync(command, reporter, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (command.ExpiresAtUtc <= now)
        {
            logger.LogWarning("Expired command {CommandId} rejected", command.CommandId);
            await reporter.FailAsync(command.CommandId, "Command expired before execution.", cancellationToken);
            return;
        }

        if (command.CreatedAtUtc > now + AllowedFutureClockSkew ||
            command.CreatedAtUtc < now - MaximumCommandAge ||
            command.CreatedAtUtc >= command.ExpiresAtUtc)
        {
            logger.LogWarning(
                "SecurityWarning: command {CommandId} has an invalid creation time",
                command.CommandId);
            return;
        }

        if (!processedCommands.TryBegin(command.CommandId, command.Nonce))
        {
            await ReportDuplicateAsync(command, reporter, cancellationToken);
            return;
        }

        logger.LogInformation(
            "Verified command {CommandId} received. Type: {CommandType}",
            command.CommandId,
            commandType);

        try
        {
            await reporter.AcknowledgeAsync(command.CommandId, cancellationToken);
            processedCommands.Set(command.CommandId, ProcessedCommandStatus.Acknowledged);
        }
        catch
        {
            processedCommands.Remove(command.CommandId);
            throw;
        }

        var executionAccepted = false;
        try
        {
            await ExecuteAsync(command, commandType, cancellationToken);
            executionAccepted = true;
            processedCommands.Set(command.CommandId, ProcessedCommandStatus.Completed);
            await reporter.CompleteAsync(command.CommandId, cancellationToken);
            logger.LogInformation("Command {CommandId} completed", command.CommandId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (powerCommand && executionAccepted)
            {
                // Keep Acknowledged/Completed in the persistent ledger: never retry the native request or clear its lease.
                logger.LogWarning("Power command {CommandId} was accepted locally but persistence/reporting failed ({ErrorType})",
                    command.CommandId, exception.GetType().Name);
                throw;
            }
            if (processedCommands.TryGet(command.CommandId, out var state) &&
                state.Status == ProcessedCommandStatus.Completed)
            {
                logger.LogWarning(
                    exception,
                    "Command {CommandId} completed locally, but completion reporting failed",
                    command.CommandId);
                throw;
            }

            var safeError = SanitizeError(exception.Message);
            processedCommands.Set(command.CommandId, ProcessedCommandStatus.Failed, safeError);
            logger.LogError(exception, "Command {CommandId} failed", command.CommandId);
            await reporter.FailAsync(command.CommandId, safeError, cancellationToken);
        }
    }

    private static bool ValidateEnvelopeStructure(
        SignedAgentCommandEnvelope command,
        StationCredentialData credential,
        out AgentCommandType commandType,
        out string rejection)
    {
        commandType = default;
        rejection = "Invalid command envelope.";
        if (command.CommandId == Guid.Empty || command.StationId != credential.StationId)
        {
            return false;
        }

        if (!Enum.TryParse(command.Type, ignoreCase: false, out commandType) ||
            !Enum.IsDefined(commandType) || command.Type != commandType.ToString())
        {
            rejection = "Command type is not in the allowlist.";
            return false;
        }

        if ((IsPowerCommand(commandType) || commandType == AgentCommandType.LaunchGame) &&
            command.ExpiresAtUtc - command.CreatedAtUtc > StationPowerPolicy.CommandLifetime)
        {
            rejection = "Power command lifetime exceeds 30 seconds.";
            return false;
        }

        byte[] nonce;
        try
        {
            nonce = SecurityEncoding.FromBase64Url(command.Nonce);
        }
        catch (FormatException)
        {
            rejection = "Command nonce is invalid.";
            return false;
        }

        if (nonce.Length is < 16 or > 64 ||
            Encoding.UTF8.GetByteCount(command.PayloadJson ?? string.Empty) >
            CommandEnvelopeCryptography.MaximumPayloadBytes)
        {
            rejection = "Command nonce or payload size is invalid.";
            return false;
        }

        return true;
    }

    private static bool TryValidatePayload(
        SignedAgentCommandEnvelope command,
        AgentCommandType commandType,
        out string error)
    {
        error = string.Empty;
        if (commandType == AgentCommandType.LaunchGame)
        {
            try
            {
                var game = JsonSerializer.Deserialize<LaunchGamePayload>(command.PayloadJson ?? "null", PayloadSerializerOptions);
                if (game is not null && game.GameId != Guid.Empty && game.GamingSessionId != Guid.Empty && game.PlayniteGameId != Guid.Empty)
                    return true;
            }
            catch (JsonException) { }
            error = "LaunchGame requires only valid game, session and Playnite IDs.";
            return false;
        }
        if (commandType is AgentCommandType.Ping or
            AgentCommandType.LockStation or
            AgentCommandType.UnlockStation or
            AgentCommandType.LogoutPlayer or
            AgentCommandType.RestartStation or
            AgentCommandType.ShutdownStation)
        {
            if (command.PayloadJson is null)
            {
                return true;
            }

            error = $"{commandType} does not accept a payload.";
            return false;
        }

        try
        {
            var payload = string.IsNullOrWhiteSpace(command.PayloadJson)
                ? null
                : JsonSerializer.Deserialize<TestMessagePayload>(
                    command.PayloadJson,
                    PayloadSerializerOptions);
            if (string.IsNullOrWhiteSpace(payload?.Message) ||
                payload.Message.Length > CommandEnvelopeCryptography.MaximumTestMessageLength)
            {
                error = "TestMessage must contain a non-empty message up to 1000 characters.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "TestMessage payload is malformed.";
            return false;
        }
    }

    private async Task ExecuteAsync(
        SignedAgentCommandEnvelope command,
        AgentCommandType commandType,
        CancellationToken cancellationToken)
    {
        switch (commandType)
        {
            case AgentCommandType.LaunchGame:
                if (games is null || playnite is null) throw new InvalidOperationException("PLAYNITE_UNAVAILABLE");
                var game = JsonSerializer.Deserialize<LaunchGamePayload>(command.PayloadJson!, PayloadSerializerOptions)!;
                var action = await games.PrepareAsync(command.CommandId, game.GameId, game.GamingSessionId,
                    game.PlayniteGameId, command.ExpiresAtUtc, cancellationToken);
                var result = await playnite.ExecutePlayniteAsync(action, cancellationToken);
                if (!result.Success) throw new InvalidOperationException("PLAYNITE_LAUNCH_FAILED");
                break;
            case AgentCommandType.RestartStation:
            case AgentCommandType.ShutdownStation:
                var current = await clientState.GetCurrentAsync(cancellationToken);
                if (!clientState.SessionReconciliationCompleted || current.State is ClientShellState.Offline or ClientShellState.SessionActive ||
                    current.UserId is not null || current.PlayerAuthSessionId is not null || current.GamingSession is not null)
                    throw new InvalidOperationException("Station power requires confirmed idle player and gaming state.");
                if (command.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
                    throw new InvalidOperationException("Power command expired before the OS request.");
                // No persisted Maintenance mutation: an OS veto or Agent restart must not strand the shell.
                if (commandType == AgentCommandType.RestartStation)
                    await restartStation.ExecuteAsync(cancellationToken);
                else
                    await shutdownStation.ExecuteAsync(cancellationToken);
                logger.LogInformation("Power command {CommandId} accepted by the OS with a 30-second graceful delay", command.CommandId);
                break;
            case AgentCommandType.Ping:
                logger.LogInformation("Received Ping command {CommandId}", command.CommandId);
                break;
            case AgentCommandType.TestMessage:
                var payload = JsonSerializer.Deserialize<TestMessagePayload>(
                    command.PayloadJson!,
                    PayloadSerializerOptions)!;
                logger.LogInformation(
                    "TestMessage command {CommandId}: {TestMessage}",
                    command.CommandId,
                    payload.Message);
                break;
            case AgentCommandType.LockStation:
                var lockedState = await clientState.SetStateAsync(
                    ClientShellState.Locked,
                    "Обратитесь к оператору",
                    cancellationToken);
                var lockDelivery = await clientNotifier.PublishAsync(
                    lockedState,
                    waitForAcknowledgement: true,
                    cancellationToken);
                if (lockDelivery.ClientConnected && !lockDelivery.Acknowledged)
                {
                    throw new TimeoutException("GameClub Client did not acknowledge the Locked state.");
                }

                logger.LogInformation(
                    "Client shell state changed to Locked for command {CommandId}",
                    command.CommandId);
                break;
            case AgentCommandType.UnlockStation:
                var availableState = await clientState.SetStateAsync(
                    ClientShellState.Available,
                    null,
                    cancellationToken);
                await clientNotifier.PublishAsync(
                    availableState,
                    waitForAcknowledgement: false,
                    cancellationToken);
                logger.LogInformation(
                    "Client shell state changed to Available for command {CommandId}",
                    command.CommandId);
                break;
            case AgentCommandType.LogoutPlayer:
                var loggedOutState = await playerLoginService.ForceLogoutAsync(cancellationToken);
                var logoutDelivery = await clientNotifier.PublishAsync(
                    loggedOutState,
                    waitForAcknowledgement: true,
                    cancellationToken);
                if (logoutDelivery.ClientConnected && !logoutDelivery.Acknowledged)
                {
                    throw new TimeoutException("GameClub Client did not acknowledge forced player logout.");
                }

                logger.LogInformation(
                    "Player was logged out for command {CommandId}",
                    command.CommandId);
                break;
            default:
                throw new InvalidOperationException("Command type is not allowed.");
        }
    }

    private async Task ReportDuplicateAsync(
        SignedAgentCommandEnvelope command,
        IAgentCommandReporter reporter,
        CancellationToken cancellationToken)
    {
        if (!processedCommands.TryGet(command.CommandId, out var state))
        {
            logger.LogWarning(
                "SecurityWarning: command {CommandId} reused a previously processed nonce",
                command.CommandId);
            return;
        }

        logger.LogInformation(
            "Duplicate command {CommandId} ignored. Local state: {CommandState}",
            command.CommandId,
            state.Status);
        switch (state.Status)
        {
            case ProcessedCommandStatus.Completed:
                await reporter.CompleteAsync(command.CommandId, cancellationToken);
                break;
            case ProcessedCommandStatus.Failed:
                await reporter.FailAsync(
                    command.CommandId,
                    state.Error ?? "Command previously failed.",
                    cancellationToken);
                break;
            case ProcessedCommandStatus.Received:
            case ProcessedCommandStatus.Acknowledged:
                await reporter.AcknowledgeAsync(command.CommandId, cancellationToken);
                break;
        }
    }

    private static string SanitizeError(string? error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "Command execution failed." : error.Trim();
        return value.Length <= MaximumErrorLength ? value : value[..MaximumErrorLength];
    }

    private static bool IsPowerCommand(AgentCommandType type) =>
        type is AgentCommandType.RestartStation or AgentCommandType.ShutdownStation;
}
