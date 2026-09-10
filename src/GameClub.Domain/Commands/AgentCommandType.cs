namespace GameClub.Domain.Commands;

public enum AgentCommandType
{
    Ping = 0,
    TestMessage = 1,
    LockStation = 2,
    UnlockStation = 3,
    LogoutPlayer = 4,
    RestartStation = 5,
    ShutdownStation = 6,
    LaunchGame = 7
}
