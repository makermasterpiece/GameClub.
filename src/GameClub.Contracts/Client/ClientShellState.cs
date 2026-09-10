namespace GameClub.Contracts.Client;

public enum ClientShellState
{
    Locked = 0,
    Available = 1,
    SessionActive = 2,
    Maintenance = 3,
    Offline = 4
}
