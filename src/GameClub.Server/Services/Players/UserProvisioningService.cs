using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GameClub.Server.Services.Players;

public enum CreateUserError
{
    None,
    InvalidUsername,
    InvalidPassword,
    InvalidDisplayName,
    InvalidContact,
    DuplicateUsername
}

public sealed record CreateUserResult(User? User, CreateUserError Error)
{
    public bool Succeeded => User is not null && Error == CreateUserError.None;
}

public interface IUserProvisioningService
{
    Task<CreateUserResult> CreateAsync(
        string username,
        string password,
        string? displayName,
        string? email,
        string? phone,
        CancellationToken cancellationToken);
}

public sealed class UserProvisioningService(
    GameClubDbContext dbContext,
    IPasswordHasherService passwordHasher,
    TimeProvider timeProvider,
    ILogger<UserProvisioningService> logger) : IUserProvisioningService
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    public async Task<CreateUserResult> CreateAsync(
        string username,
        string password,
        string? displayName,
        string? email,
        string? phone,
        CancellationToken cancellationToken)
    {
        var trimmedUsername = username?.Trim() ?? string.Empty;
        if (trimmedUsername.Length is < User.MinimumUsernameLength or > User.MaximumUsernameLength)
        {
            return new CreateUserResult(null, CreateUserError.InvalidUsername);
        }

        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            return new CreateUserResult(null, CreateUserError.InvalidPassword);
        }

        if (displayName?.Trim().Length > User.MaximumDisplayNameLength)
        {
            return new CreateUserResult(null, CreateUserError.InvalidDisplayName);
        }

        if (email?.Trim().Length > User.MaximumEmailLength ||
            phone?.Trim().Length > User.MaximumPhoneLength)
        {
            return new CreateUserResult(null, CreateUserError.InvalidContact);
        }

        var normalizedUsername = User.NormalizeUsername(trimmedUsername);
        if (await dbContext.Users.AnyAsync(
                user => user.NormalizedUsername == normalizedUsername,
                cancellationToken))
        {
            return new CreateUserResult(null, CreateUserError.DuplicateUsername);
        }

        var user = new User(
            Guid.NewGuid(),
            trimmedUsername,
            displayName,
            email,
            phone,
            timeProvider.GetUtcNow().UtcDateTime);
        user.SetPasswordHash(passwordHasher.HashPassword(user, password));
        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_users_normalized_username"
            })
        {
            dbContext.Entry(user).State = EntityState.Detached;
            return new CreateUserResult(null, CreateUserError.DuplicateUsername);
        }

        logger.LogInformation("Development user {UserId} created", user.Id);
        return new CreateUserResult(user, CreateUserError.None);
    }
}
