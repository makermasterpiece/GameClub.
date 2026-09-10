using GameClub.Domain.Commands;

namespace GameClub.Agent.Services.Commands;

public interface IAgentCommandHandler
{
    Task HandleAsync(
        SignedAgentCommandEnvelope command,
        IAgentCommandReporter reporter,
        CancellationToken cancellationToken);
}
