using System.Security.Cryptography;
using GameClub.Application.Abstractions;
using GameClub.Domain.Commands;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services.Players;

public enum PlayerLoginError
{
    None,
    InvalidCredentials,
    AccountDisabled,
    AccountBanned,
    UserAlreadyLoggedIn,
    StationAlreadyLoggedIn,
    StationUnavailable,
    RateLimited
}

public sealed record PlayerSessionData(
    Guid SessionId,
    Guid UserId,
    string Username,
    string? DisplayName,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record PlayerLoginResult(PlayerSessionData? Session, PlayerLoginError Error)
{
    public bool Succeeded => Session is not null && Error == PlayerLoginError.None;
}

public interface IPlayerAuthenticationService
{
    Task<PlayerLoginResult> LoginAsync(
        Guid stationId,
        string username,
        string password,
        string? sourceIp,
        CancellationToken cancellationToken);

    Task<PlayerSessionData?> GetCurrentAsync(Guid stationId, CancellationToken cancellationToken);

    Task<bool> LogoutAsync(
        Guid stationId,
        string? sourceIp,
        CancellationToken cancellationToken);
}

public sealed class PlayerAuthenticationService(
    GameClubDbContext dbContext,
    IClubData data,
    IPasswordHasherService passwordHasher,
    IPlayerLoginRateLimiter rateLimiter,
    ISecurityAuditService audit,
    TimeProvider timeProvider,
    ILogger<PlayerAuthenticationService> logger) : IPlayerAuthenticationService
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly Lazy<User> UnknownUser = new(CreateUnknownUser);

    public async Task<PlayerLoginResult> LoginAsync(
        Guid stationId,
        string username,
        string password,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!rateLimiter.TryBegin(stationId, now))
        {
            await AuditAsync(
                "PlayerLoginRateLimited",
                stationId,
                null,
                sourceIp,
                "Result=RATE_LIMITED",
                cancellationToken);
            return new PlayerLoginResult(null, PlayerLoginError.RateLimited);
        }

        try
        {
            // Share the SERIALIZABLE boundary with power commands and station transfers.
            // The non-relational branch supports the isolated authentication unit tests.
            return dbContext.Database.IsRelational()
                ? await data.AtomicAsync(ct => LoginCoreAsync(stationId, username, password, sourceIp, ct), cancellationToken)
                : await LoginCoreAsync(stationId, username, password, sourceIp, cancellationToken);
        }
        catch (ClubException exception) when (exception.Code == "CONCURRENT_CONFLICT")
        {
            var normalized = User.NormalizeUsername(username.Trim());
            var userId = await dbContext.Users.AsNoTracking().Where(u => u.NormalizedUsername == normalized)
                .Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken);
            if (userId is not null)
            {
                var conflict = await ResolveConcurrentConflictAsync(stationId, userId.Value, sourceIp, cancellationToken);
                if (conflict is not null) return conflict;
            }
            throw;
        }
    }

    private async Task<PlayerLoginResult> LoginCoreAsync(Guid stationId, string username, string password,
        string? sourceIp, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var station = await dbContext.Stations.FindAsync([stationId], cancellationToken);
        var powerCutoff = now - StationPowerPolicy.BusyGrace;
        var pendingPower = await dbContext.AgentCommands.AnyAsync(command => command.StationId == stationId &&
            (command.Type == AgentCommandType.RestartStation || command.Type == AgentCommandType.ShutdownStation) &&
            command.Status != AgentCommandStatus.Failed && command.Status != AgentCommandStatus.Expired &&
            command.ExpiresAtUtc > powerCutoff, cancellationToken);
        if (station is null || station.Status != StationStatus.Online || pendingPower)
        {
            return await RejectAsync(
                stationId,
                null,
                PlayerLoginError.StationUnavailable,
                sourceIp,
                cancellationToken);
        }

        var trimmedUsername = username?.Trim() ?? string.Empty;
        if (trimmedUsername.Length is < User.MinimumUsernameLength or > User.MaximumUsernameLength ||
            password is null || password.Length is 0 or > UserProvisioningService.MaximumPasswordLength)
        {
            return await RejectAsync(
                stationId,
                null,
                PlayerLoginError.InvalidCredentials,
                sourceIp,
                cancellationToken);
        }

        var normalizedUsername = User.NormalizeUsername(trimmedUsername);
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.NormalizedUsername == normalizedUsername,
            cancellationToken);
        if (user is null)
        {
            // Pay the same password-verification cost without accepting a synthetic identity.
            _ = passwordHasher.VerifyPassword(UnknownUser.Value, password);
            return await RejectAsync(
                stationId,
                null,
                PlayerLoginError.InvalidCredentials,
                sourceIp,
                cancellationToken);
        }

        var verification = passwordHasher.VerifyPassword(user, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return await RejectAsync(
                stationId,
                user.Id,
                PlayerLoginError.InvalidCredentials,
                sourceIp,
                cancellationToken);
        }

        if (user.Status == UserStatus.Disabled)
        {
            return await RejectAsync(
                stationId,
                user.Id,
                PlayerLoginError.AccountDisabled,
                sourceIp,
                cancellationToken);
        }

        if (user.Status == UserStatus.Banned)
        {
            return await RejectAsync(
                stationId,
                user.Id,
                PlayerLoginError.AccountBanned,
                sourceIp,
                cancellationToken);
        }

        var candidates = await dbContext.PlayerAuthSessions
            .Include(session => session.User)
            .Where(session =>
                session.Status == PlayerAuthSessionStatus.Active &&
                (session.UserId == user.Id || session.StationId == stationId))
            .ToListAsync(cancellationToken);
        var expiredChanged = false;
        foreach (var candidate in candidates)
        {
            expiredChanged |= RefreshSession(candidate, now);
        }

        if (expiredChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (candidates.Any(session => session.UserId == user.Id && session.IsActiveAt(now)))
        {
            return await RejectDuplicateAsync(
                stationId,
                user.Id,
                PlayerLoginError.UserAlreadyLoggedIn,
                sourceIp,
                cancellationToken);
        }

        if (candidates.Any(session => session.StationId == stationId && session.IsActiveAt(now)))
        {
            return await RejectDuplicateAsync(
                stationId,
                user.Id,
                PlayerLoginError.StationAlreadyLoggedIn,
                sourceIp,
                cancellationToken);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.SetPasswordHash(passwordHasher.HashPassword(user, password));
        }

        var session = new PlayerAuthSession(
            Guid.NewGuid(),
            user.Id,
            stationId,
            now,
            now + SessionLifetime);
        user.RecordLogin(now);
        dbContext.PlayerAuthSessions.Add(session);

        await dbContext.SaveChangesAsync(cancellationToken);

        rateLimiter.RecordSuccess(stationId, now);
        await AuditAsync(
            "PlayerLoginSucceeded",
            stationId,
            user.Id,
            sourceIp,
            $"SessionId={session.Id:D};Result=Success",
            cancellationToken);
        logger.LogInformation(
            "Player {UserId} logged in at station {StationId}; session {SessionId}",
            user.Id,
            stationId,
            session.Id);
        return new PlayerLoginResult(ToData(session, user), PlayerLoginError.None);
    }

    public async Task<PlayerSessionData?> GetCurrentAsync(
        Guid stationId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await dbContext.PlayerAuthSessions
            .Include(session => session.User)
            .Where(session =>
                session.StationId == stationId &&
                session.Status == PlayerAuthSessionStatus.Active)
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var session in sessions)
        {
            changed |= RefreshSession(session, now);
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var active = sessions.SingleOrDefault(session => session.IsActiveAt(now));
        return active is null ? null : ToData(active, active.User);
    }

    public async Task<bool> LogoutAsync(
        Guid stationId,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var session = await dbContext.PlayerAuthSessions
            .SingleOrDefaultAsync(
                candidate => candidate.StationId == stationId &&
                             candidate.Status == PlayerAuthSessionStatus.Active,
                cancellationToken);
        if (session is null)
        {
            return true;
        }

        var wasActive = session.IsActiveAt(now);
        session.End(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (wasActive)
        {
            await AuditAsync(
                "PlayerLogout",
                stationId,
                session.UserId,
                sourceIp,
                $"SessionId={session.Id:D};Result=Success",
                cancellationToken);
            logger.LogInformation(
                "Player {UserId} logged out from station {StationId}; session {SessionId}",
                session.UserId,
                stationId,
                session.Id);
        }

        return true;
    }

    private async Task<PlayerLoginResult> RejectAsync(
        Guid stationId,
        Guid? userId,
        PlayerLoginError error,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        rateLimiter.RecordFailure(stationId, now);
        await AuditAsync(
            "PlayerLoginFailed",
            stationId,
            userId,
            sourceIp,
            $"Result={ToCode(error)}",
            cancellationToken);
        logger.LogWarning(
            "Player login rejected for station {StationId} and user {UserId}. Result: {Result}",
            stationId,
            userId,
            ToCode(error));
        return new PlayerLoginResult(null, error);
    }

    private async Task<PlayerLoginResult> RejectDuplicateAsync(
        Guid stationId,
        Guid userId,
        PlayerLoginError error,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        rateLimiter.RecordFailure(stationId, timeProvider.GetUtcNow().UtcDateTime);
        await AuditAsync(
            "DuplicateLoginRejected",
            stationId,
            userId,
            sourceIp,
            $"Result={ToCode(error)}",
            cancellationToken);
        return new PlayerLoginResult(null, error);
    }

    private async Task<PlayerLoginResult?> ResolveConcurrentConflictAsync(
        Guid stationId,
        Guid userId,
        string? sourceIp,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var userHasSession = await dbContext.PlayerAuthSessions.AsNoTracking().AnyAsync(
            session => session.UserId == userId &&
                       session.Status == PlayerAuthSessionStatus.Active &&
                       session.ExpiresAtUtc > now,
            cancellationToken);
        var stationHasSession = await dbContext.PlayerAuthSessions.AsNoTracking().AnyAsync(
            session => session.StationId == stationId &&
                       session.Status == PlayerAuthSessionStatus.Active &&
                       session.ExpiresAtUtc > now,
            cancellationToken);
        if (!userHasSession && !stationHasSession)
        {
            return null;
        }

        return await RejectDuplicateAsync(
            stationId,
            userId,
            userHasSession
                ? PlayerLoginError.UserAlreadyLoggedIn
                : PlayerLoginError.StationAlreadyLoggedIn,
            sourceIp,
            cancellationToken);
    }

    private Task AuditAsync(
        string eventType,
        Guid stationId,
        Guid? userId,
        string? sourceIp,
        string details,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(
            eventType,
            stationId,
            sourceIp,
            details,
            cancellationToken,
            userId);

    public static string ToCode(PlayerLoginError error) => error switch
    {
        PlayerLoginError.InvalidCredentials => "INVALID_CREDENTIALS",
        PlayerLoginError.AccountDisabled => "ACCOUNT_DISABLED",
        PlayerLoginError.AccountBanned => "ACCOUNT_BANNED",
        PlayerLoginError.UserAlreadyLoggedIn => "USER_ALREADY_LOGGED_IN",
        PlayerLoginError.StationAlreadyLoggedIn => "STATION_ALREADY_LOGGED_IN",
        PlayerLoginError.StationUnavailable => "STATION_UNAVAILABLE",
        PlayerLoginError.RateLimited => "RATE_LIMITED",
        _ => string.Empty
    };

    private static PlayerSessionData ToData(PlayerAuthSession session, User user) =>
        new(
            session.Id,
            user.Id,
            user.Username,
            user.DisplayName,
            session.CreatedAtUtc,
            session.ExpiresAtUtc);

    private static bool RefreshSession(PlayerAuthSession session, DateTime utcNow)
    {
        var changed = session.ExpireIfNeeded(utcNow);
        if (session.User.Status != UserStatus.Active)
        {
            changed |= session.End(utcNow);
        }

        return changed;
    }

    private static User CreateUnknownUser()
    {
        var user = new User(Guid.NewGuid(), "unknown-user", null, null, null, DateTime.UnixEpoch);
        // A process-local hash uses the same Identity V3 defaults as registered password hashing.
        // It is never persisted and the random password is not retained.
        user.SetPasswordHash(new PasswordHasher<User>().HashPassword(
            user,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        return user;
    }
}
