using GameClub.Domain.Commands;

namespace GameClub.Server.Services.Commands;

public interface IAgentCommandService
{
    Task<AgentCommand?> CreateCommandAsync(
        Guid stationId,
        AgentCommandType type,
        string? payloadJson,
        CancellationToken cancellationToken);

    Task<AgentCommand?> DispatchCommandAsync(Guid commandId, CancellationToken cancellationToken);

    Task DispatchPendingCommandsAsync(Guid stationId, CancellationToken cancellationToken);

    Task<CommandOperationResult> AcknowledgeAsync(
        Guid commandId,
        Guid authenticatedStationId,
        CancellationToken cancellationToken);

    Task<CommandOperationResult> CompleteAsync(
        Guid commandId,
        Guid authenticatedStationId,
        CancellationToken cancellationToken);

    Task<CommandOperationResult> FailAsync(
        Guid commandId,
        Guid authenticatedStationId,
        string? error,
        CancellationToken cancellationToken);
}
