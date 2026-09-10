using GameClub.Domain.Users;

namespace GameClub.Domain.Stations;

public sealed class Station
{
    public static readonly TimeSpan ClientHeartbeatTimeout = TimeSpan.FromSeconds(15);
    private Station()
    {
    }

    public Station(
        Guid id,
        string name,
        string machineName,
        string? ipAddress,
        string agentVersion,
        DateTime utcNow)
    {
        Id = id;
        CreatedAtUtc = utcNow;
        UpdateFromAgent(name, machineName, ipAddress, agentVersion, utcNow);
    }

    public Guid Id { get; private set; }

    public Guid? StationGroupId { get; private set; } = Guid.Parse("10000000-0000-0000-0000-000000000001");

    public void AssignGroup(Guid groupId)
    {
        if (groupId == Guid.Empty) throw new ArgumentException("Station group is required.", nameof(groupId));
        StationGroupId = groupId;
    }

    public string Name { get; private set; } = string.Empty;

    public string MachineName { get; private set; } = string.Empty;

    public string? IpAddress { get; private set; }

    public string AgentVersion { get; private set; } = string.Empty;

    public StationStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime LastSeenAtUtc { get; private set; }

    public bool ClientConnected { get; private set; }
    public string ClientState { get; private set; } = "Offline";
    public DateTime? ClientLastSeenAtUtc { get; private set; }

    public bool UpdateClientHealth(bool connected, string? state, DateTime? lastSeenAtUtc, DateTime serverNow)
    {
        if (serverNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Server health timestamps must be UTC.", nameof(serverNow));
        var validTime = lastSeenAtUtc is { Kind: DateTimeKind.Utc } seen &&
                        seen >= DateTime.UnixEpoch && seen <= serverNow;
        var validState = state is "Offline" or "Locked" or "Available" or "SessionActive" or "Maintenance";
        var fresh = connected && validTime && validState && serverNow - lastSeenAtUtc!.Value <= ClientHeartbeatTimeout;
        var reportedState = fresh ? state! : "Offline";
        var changed = ClientConnected != fresh || ClientState != reportedState;
        ClientConnected = fresh;
        ClientState = reportedState;
        ClientLastSeenAtUtc = validTime ? lastSeenAtUtc : null;
        return changed;
    }

    public ICollection<PlayerAuthSession> PlayerAuthSessions { get; private set; } = [];

    public bool UpdateFromAgent(
        string name,
        string machineName,
        string? ipAddress,
        string agentVersion,
        DateTime utcNow)
    {
        var statusChanged = Status != StationStatus.Online;

        Name = name.Trim();
        MachineName = machineName.Trim();
        IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress;
        AgentVersion = agentVersion.Trim();
        LastSeenAtUtc = utcNow;
        Status = StationStatus.Online;

        return statusChanged;
    }

    public bool MarkOffline(DateTime utcNow, TimeSpan heartbeatTimeout)
    {
        if (Status != StationStatus.Online || utcNow - LastSeenAtUtc <= heartbeatTimeout)
        {
            return false;
        }

        Status = StationStatus.Offline;
        return true;
    }
}
