using System.ComponentModel.DataAnnotations;
using GameClub.Domain.Users;

namespace GameClub.Server.Contracts.Users;

public sealed record CreateDevelopmentUserRequest(
    [param: Required, MinLength(User.MinimumUsernameLength), MaxLength(User.MaximumUsernameLength)]
    string Username,
    [param: Required, MinLength(12), MaxLength(128)]
    string Password,
    [param: MaxLength(User.MaximumDisplayNameLength)]
    string? DisplayName,
    [param: EmailAddress, MaxLength(User.MaximumEmailLength)]
    string? Email = null,
    [param: MaxLength(User.MaximumPhoneLength)]
    string? Phone = null);

public sealed record DevelopmentUserResponse(
    Guid Id,
    string Username,
    string? DisplayName,
    string? Email,
    string? Phone,
    UserStatus Status,
    DateTime CreatedAtUtc);
