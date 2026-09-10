using System.ComponentModel.DataAnnotations;
using GameClub.Domain.Users;

namespace GameClub.Server.Contracts.Players;

public sealed record PlayerLoginRequest(
    [param: Required, MaxLength(User.MaximumUsernameLength)] string Username,
    [param: Required, MaxLength(128)] string Password);

public sealed record PlayerUserResponse(
    Guid Id,
    string Username,
    string? DisplayName);

public sealed record PlayerSessionResponse(
    bool Success,
    Guid SessionId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    PlayerUserResponse User);

public sealed record PlayerLogoutResponse(bool Success);

public sealed record PlayerLogoutRequest([Required] Guid? ExpectedSessionId);

public sealed record PlayerErrorResponse(bool Success, string Code);
