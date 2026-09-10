using GameClub.Domain.Commands;

namespace GameClub.Server.Contracts.Commands;

internal static class AgentCommandMappings
{
    public static AgentCommandResponse ToResponse(this AgentCommand command) =>
        new(
            command.Id,
            command.StationId,
            command.Type,
            command.Status,
            command.CreatedAtUtc,
            command.ExpiresAtUtc,
            command.SentAtUtc,
            command.AcknowledgedAtUtc,
            command.CompletedAtUtc,
            command.FailedAtUtc,
            command.ErrorMessage);
}
