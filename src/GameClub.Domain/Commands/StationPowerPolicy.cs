namespace GameClub.Domain.Commands;

public static class StationPowerPolicy
{
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(30);

    public static bool IsPowerType(AgentCommandType type) =>
        type is AgentCommandType.RestartStation or AgentCommandType.ShutdownStation;
}
