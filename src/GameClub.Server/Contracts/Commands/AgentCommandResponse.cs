using GameClub.Domain.Commands;

namespace GameClub.Server.Contracts.Commands;

public sealed record AgentCommandResponse(
    Guid Id,
    Guid StationId,
    AgentCommandType Type,
    AgentCommandStatus Status,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? SentAtUtc,
    DateTime? AcknowledgedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? FailedAtUtc,
    string? ErrorMessage);
