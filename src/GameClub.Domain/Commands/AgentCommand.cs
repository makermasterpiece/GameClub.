using GameClub.Domain.Stations;

namespace GameClub.Domain.Commands;

public sealed class AgentCommand
{
    private AgentCommand()
    {
    }

    public AgentCommand(
        Guid id,
        Guid stationId,
        AgentCommandType type,
        string? payloadJson,
        DateTime createdAtUtc,
        DateTime expiresAtUtc)
    {
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Command expiration must be later than creation time.");
        }

        Id = id;
        StationId = stationId;
        Type = type;
        PayloadJson = payloadJson;
        Status = AgentCommandStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid StationId { get; private set; }

    public AgentCommandType Type { get; private set; }

    public string? PayloadJson { get; private set; }

    public AgentCommandStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? SentAtUtc { get; private set; }

    public DateTime? AcknowledgedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public DateTime? FailedAtUtc { get; private set; }

    public string? ErrorMessage { get; private set; }

    public Station Station { get; private set; } = null!;

    public bool MarkSent(DateTime utcNow)
    {
        if (IsExpiredAt(utcNow))
        {
            MarkExpired(utcNow);
            return false;
        }

        if (Status is not (AgentCommandStatus.Pending or AgentCommandStatus.Sent))
        {
            return false;
        }

        Status = AgentCommandStatus.Sent;
        SentAtUtc ??= utcNow;
        return true;
    }

    public bool ReturnToPendingAfterDispatchFailure()
    {
        if (Status != AgentCommandStatus.Sent || AcknowledgedAtUtc is not null)
        {
            return false;
        }

        Status = AgentCommandStatus.Pending;
        SentAtUtc = null;
        return true;
    }

    public bool Acknowledge(DateTime utcNow)
    {
        if (IsExpiredAt(utcNow))
        {
            MarkExpired(utcNow);
            return false;
        }

        if (Status == AgentCommandStatus.Acknowledged)
        {
            return true;
        }

        if (Status != AgentCommandStatus.Sent)
        {
            return false;
        }

        Status = AgentCommandStatus.Acknowledged;
        SentAtUtc ??= utcNow;
        AcknowledgedAtUtc = utcNow;
        return true;
    }

    public bool Complete(DateTime utcNow)
    {
        if (Status == AgentCommandStatus.Completed)
        {
            return true;
        }

        if (Status != AgentCommandStatus.Acknowledged)
        {
            return false;
        }

        Status = AgentCommandStatus.Completed;
        CompletedAtUtc = utcNow;
        ErrorMessage = null;
        return true;
    }

    public bool Fail(DateTime utcNow, string errorMessage)
    {
        if (Status == AgentCommandStatus.Failed)
        {
            return true;
        }

        if (Status is not (AgentCommandStatus.Sent or AgentCommandStatus.Acknowledged))
        {
            return false;
        }

        Status = AgentCommandStatus.Failed;
        FailedAtUtc = utcNow;
        ErrorMessage = errorMessage;
        return true;
    }

    public bool MarkExpired(DateTime utcNow)
    {
        if (!IsExpiredAt(utcNow) ||
            Status is AgentCommandStatus.Completed or AgentCommandStatus.Failed or AgentCommandStatus.Expired)
        {
            return false;
        }

        Status = AgentCommandStatus.Expired;
        return true;
    }

    public bool RejectPowerDispatch(DateTime utcNow, string errorMessage)
    {
        if (!StationPowerPolicy.IsPowerType(Type) || Status is not (AgentCommandStatus.Pending or AgentCommandStatus.Sent))
            return false;
        Status = AgentCommandStatus.Failed;
        FailedAtUtc = utcNow;
        ErrorMessage = errorMessage;
        return true;
    }

    public bool RejectGameLaunch(DateTime utcNow, string errorMessage)
    {
        if (Type != AgentCommandType.LaunchGame ||
            Status is not (AgentCommandStatus.Pending or AgentCommandStatus.Sent or AgentCommandStatus.Acknowledged))
            return false;
        Status = AgentCommandStatus.Failed;
        FailedAtUtc = utcNow;
        ErrorMessage = errorMessage;
        return true;
    }

    private bool IsExpiredAt(DateTime utcNow) => utcNow >= ExpiresAtUtc;
}
