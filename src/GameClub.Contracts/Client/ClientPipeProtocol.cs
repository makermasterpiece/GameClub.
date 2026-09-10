using GameClub.Contracts.Games;

namespace GameClub.Contracts.Client;

public static class ClientPipeProtocol
{
    public const string PipeName = "GameClub.Agent.ClientState";
    public const int MaximumMessageBytes = 16 * 1024;
}

public enum ClientPipeRequestType
{
    GetState = 0,
    Heartbeat = 1,
    AcknowledgeState = 2,
    PlayerLogin = 3,
    PlayerLogout = 4,
    OpenGames = 5,
    PlayniteActionResult = 6
}

public sealed record PlayerLoginRequest(
    Guid RequestId,
    string Username,
    string Password);

public sealed record PlayerLogoutRequest(Guid RequestId);

public sealed record ClientPipeRequest(
    ClientPipeRequestType Type,
    long? Revision = null,
    PlayerLoginRequest? Login = null,
    PlayerLogoutRequest? Logout = null,
    GamesMenuRequest? OpenGames = null,
    PlayniteActionResult? PlayniteResult = null);

public enum ClientPipeResponseType
{
    State = 0,
    PlayerLoginResult = 1,
    PlayerLogoutResult = 2,
    PlayniteAction = 3,
    GamesMenuResult = 4
}

public sealed record ClientPlayerIdentity(
    Guid Id,
    string Username,
    string? DisplayName);

public sealed record PlayerLoginResultMessage(
    Guid RequestId,
    bool Success,
    string? ErrorCode,
    Guid? SessionId,
    ClientPlayerIdentity? User);

public sealed record PlayerLogoutResultMessage(
    Guid RequestId,
    bool Success,
    string? ErrorCode);

public sealed record ClientPipeResponse(
    ClientPipeResponseType Type,
    ClientStateMessage? State = null,
    PlayerLoginResultMessage? LoginResult = null,
    PlayerLogoutResultMessage? LogoutResult = null,
    PlayniteActionRequest? PlayniteAction = null,
    GamesMenuResult? GamesMenuResult = null);
