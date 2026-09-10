using GameClub.Domain.Commands;

namespace GameClub.Server.Contracts.Commands;

public sealed record CreateAgentCommandResponse(
    Guid CommandId,
    Guid StationId,
    AgentCommandType Type,
    AgentCommandStatus Status,
    DateTime CreatedAtUtc,
    DateTime? SentAtUtc);
