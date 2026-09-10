namespace GameClub.Agent.Services.Commands;

public interface IProcessedCommandStore
{
    bool TryBegin(Guid commandId, string nonce);

    bool TryGet(Guid commandId, out ProcessedCommandState state);

    void Set(Guid commandId, ProcessedCommandStatus status, string? error = null);

    void Remove(Guid commandId);
}

public sealed record ProcessedCommandState(
    ProcessedCommandStatus Status,
    string? Error,
    DateTime UpdatedAtUtc);

public enum ProcessedCommandStatus
{
    Received,
    Acknowledged,
    Completed,
    Failed
}
