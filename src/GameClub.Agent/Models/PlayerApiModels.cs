namespace GameClub.Agent.Models;

public sealed record PlayerLoginApiRequest(string Username, string Password);

public sealed record PlayerLogoutApiRequest(Guid ExpectedSessionId);

public sealed record PlayerApiUser(Guid Id, string Username, string? DisplayName);

public sealed record PlayerSessionApiResponse(
    bool Success,
    Guid SessionId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    PlayerApiUser User);

public sealed record PlayerApiErrorResponse(bool Success, string Code);

public sealed record PlayerApiOperationResult(
    bool Success,
    PlayerSessionApiResponse? Session,
    string? ErrorCode);
