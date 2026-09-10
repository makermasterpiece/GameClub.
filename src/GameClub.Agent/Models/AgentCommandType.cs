namespace GameClub.Agent.Models;

public enum AgentCommandType
{
    Ping,
    TestMessage,
    LockStation,
    UnlockStation,
    LogoutPlayer,
    RestartStation,
    ShutdownStation,
    LaunchGame
}
