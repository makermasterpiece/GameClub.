namespace GameClub.Agent.Services.Commands;

public interface IAgentCommandReporter
{
    Task AcknowledgeAsync(Guid commandId, CancellationToken cancellationToken);

    Task CompleteAsync(Guid commandId, CancellationToken cancellationToken);

    Task FailAsync(Guid commandId, string error, CancellationToken cancellationToken);
}
