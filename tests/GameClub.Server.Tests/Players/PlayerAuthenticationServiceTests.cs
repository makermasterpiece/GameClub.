using System.Text.Json;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Players;
using GameClub.Server.Contracts.Users;
using GameClub.Server.Security;
using GameClub.Server.Services.Players;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Players;

public sealed class PlayerAuthenticationServiceTests
{
    [Fact]
    public async Task CreateUser_HashesPasswordAndNormalizesUsername()
    {
        await using var fixture = CreateFixture();

        var result = await fixture.Users.CreateAsync(
            "  Nur  ",
            "StrongPassword123!",
            "Nur",
            null,
            null,
            default);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User);
        Assert.Equal("Nur", result.User.Username);
        Assert.Equal("NUR", result.User.NormalizedUsername);
        Assert.NotEqual("StrongPassword123!", result.User.PasswordHash);
        Assert.DoesNotContain("StrongPassword123!", result.User.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateUsername_WithDifferentCase_IsRejected()
    {
        await using var fixture = CreateFixture();
        await fixture.CreateUserAsync("Nur", "StrongPassword123!");

        var duplicate = await fixture.Users.CreateAsync(
            "nUR",
            "AnotherPassword123!",
            null,
            null,
            null,
            default);

        Assert.False(duplicate.Succeeded);
        Assert.Equal(CreateUserError.DuplicateUsername, duplicate.Error);
    }

    [Fact]
    public async Task Login_WithValidCredentials_CreatesSession()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        var user = await fixture.CreateUserAsync("nur", "StrongPassword123!");

        var result = await fixture.Authentication.LoginAsync(
            station.Id,
            "NUR",
            "StrongPassword123!",
            null,
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.Session?.UserId);
        Assert.Equal(station.Id, (await fixture.Db.PlayerAuthSessions.SingleAsync()).StationId);
        Assert.NotNull(user.LastLoginAtUtc);
        Assert.Equal(PlayerAuthSessionStatus.Active, (await fixture.Db.PlayerAuthSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsInvalidCredentials()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");

        var result = await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "WrongPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.InvalidCredentials, result.Error);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task UnknownUsername_HasSameExternalErrorAsWrongPassword()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");

        var wrongPassword = await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "WrongPassword123!",
            null,
            default);
        var unknownUser = await fixture.Authentication.LoginAsync(
            station.Id,
            "missing",
            "WrongPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.InvalidCredentials, wrongPassword.Error);
        Assert.Equal(wrongPassword.Error, unknownUser.Error);
        Assert.Equal(
            PlayerAuthenticationService.ToCode(wrongPassword.Error),
            PlayerAuthenticationService.ToCode(unknownUser.Error));
    }

    [Theory]
    [InlineData(PasswordVerificationResult.Failed)]
    [InlineData(PasswordVerificationResult.Success)]
    public async Task UnknownUsername_PerformsDummyVerificationButNeverAuthenticates(
        PasswordVerificationResult verificationResult)
    {
        var hasher = new RecordingPasswordHasher(verificationResult);
        await using var fixture = CreateFixture(hasher);
        var station = fixture.AddStation("PC-01", "MACHINE-01");

        var result = await fixture.Authentication.LoginAsync(
            station.Id, "missing", "WrongPassword123!", null, default);

        Assert.Equal(PlayerLoginError.InvalidCredentials, result.Error);
        Assert.Equal(1, hasher.VerificationCount);
        Assert.False(string.IsNullOrWhiteSpace(hasher.VerifiedHash));
        Assert.Empty(fixture.Db.Users);
        Assert.Empty(fixture.Db.PlayerAuthSessions);
    }

    [Fact]
    public async Task DisabledUser_IsRejected()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        var user = await fixture.CreateUserAsync("nur", "StrongPassword123!");
        user.ChangeStatus(UserStatus.Disabled);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.AccountDisabled, result.Error);
    }

    [Fact]
    public async Task BannedUser_IsRejected()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        var user = await fixture.CreateUserAsync("nur", "StrongPassword123!");
        user.ChangeStatus(UserStatus.Banned);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.AccountBanned, result.Error);
    }

    [Fact]
    public async Task SameUser_OnSecondStation_IsRejected()
    {
        await using var fixture = CreateFixture();
        var firstStation = fixture.AddStation("PC-01", "MACHINE-01");
        var secondStation = fixture.AddStation("PC-02", "MACHINE-02");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");
        await fixture.Authentication.LoginAsync(
            firstStation.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        var result = await fixture.Authentication.LoginAsync(
            secondStation.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.UserAlreadyLoggedIn, result.Error);
        Assert.Single(fixture.Db.PlayerAuthSessions);
    }

    [Fact]
    public async Task SameStation_WithSecondUser_IsRejected()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");
        await fixture.CreateUserAsync("alex", "AnotherPassword123!");
        await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        var result = await fixture.Authentication.LoginAsync(
            station.Id,
            "alex",
            "AnotherPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.StationAlreadyLoggedIn, result.Error);
        Assert.Single(fixture.Db.PlayerAuthSessions);
    }

    [Fact]
    public async Task Logout_EndsActiveSession()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");
        await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        var success = await fixture.Authentication.LogoutAsync(station.Id, null, default);
        var session = await fixture.Db.PlayerAuthSessions.SingleAsync();

        Assert.True(success);
        Assert.Equal(PlayerAuthSessionStatus.Ended, session.Status);
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public async Task CurrentSession_ReturnsLoggedInUser()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        var user = await fixture.CreateUserAsync("nur", "StrongPassword123!");
        var login = await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        var current = await fixture.Authentication.GetCurrentAsync(station.Id, default);

        Assert.NotNull(current);
        Assert.Equal(login.Session?.SessionId, current.SessionId);
        Assert.Equal(user.Id, current.UserId);
        Assert.Equal("nur", current.Username);
    }

    [Fact]
    public async Task ExpiredSession_IsRejectedByCurrentLookup()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");
        await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);
        fixture.Time.Advance(TimeSpan.FromHours(13));

        var current = await fixture.Authentication.GetCurrentAsync(station.Id, default);
        var session = await fixture.Db.PlayerAuthSessions.SingleAsync();

        Assert.Null(current);
        Assert.Equal(PlayerAuthSessionStatus.Expired, session.Status);
    }

    [Theory]
    [InlineData(UserStatus.Disabled)]
    [InlineData(UserStatus.Banned)]
    public async Task CurrentSession_ForInactiveUser_IsEnded(UserStatus status)
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        var user = await fixture.CreateUserAsync("nur", "StrongPassword123!");
        await fixture.Authentication.LoginAsync(
            station.Id, "nur", "StrongPassword123!", null, default);
        user.ChangeStatus(status);
        await fixture.Db.SaveChangesAsync();

        var current = await fixture.Authentication.GetCurrentAsync(station.Id, default);
        var session = await fixture.Db.PlayerAuthSessions.SingleAsync();

        Assert.Null(current);
        Assert.Equal(PlayerAuthSessionStatus.Ended, session.Status);
        Assert.Equal(fixture.Time.GetUtcNow().UtcDateTime, session.EndedAtUtc);
    }

    [Theory]
    [InlineData(UserStatus.Disabled)]
    [InlineData(UserStatus.Banned)]
    public async Task Login_ReleasesStationOccupiedByInactiveUser(UserStatus status)
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        var formerUser = await fixture.CreateUserAsync("nur", "StrongPassword123!");
        var newUser = await fixture.CreateUserAsync("alex", "AnotherPassword123!");
        await fixture.Authentication.LoginAsync(
            station.Id, "nur", "StrongPassword123!", null, default);
        formerUser.ChangeStatus(status);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Authentication.LoginAsync(
            station.Id, "alex", "AnotherPassword123!", null, default);

        Assert.True(result.Succeeded);
        Assert.Equal(newUser.Id, result.Session?.UserId);
        var formerSession = await fixture.Db.PlayerAuthSessions.SingleAsync(
            session => session.UserId == formerUser.Id);
        Assert.Equal(PlayerAuthSessionStatus.Ended, formerSession.Status);
    }

    [Fact]
    public async Task SuccessfulLogin_DoesNotResetFailedAttemptsWithinWindow()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await fixture.Authentication.LoginAsync(
                station.Id, "nur", "WrongPassword123!", null, default);
        }

        var success = await fixture.Authentication.LoginAsync(
            station.Id, "nur", "StrongPassword123!", null, default);
        Assert.True(success.Succeeded);
        await fixture.Authentication.LogoutAsync(station.Id, null, default);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await fixture.Authentication.LoginAsync(
                station.Id, "nur", "WrongPassword123!", null, default);
        }

        var limited = await fixture.Authentication.LoginAsync(
            station.Id, "nur", "StrongPassword123!", null, default);
        Assert.Equal(PlayerLoginError.RateLimited, limited.Error);

        fixture.Time.Advance(PlayerLoginRateLimiter.Window);
        var afterWindow = await fixture.Authentication.LoginAsync(
            station.Id, "nur", "StrongPassword123!", null, default);
        Assert.True(afterWindow.Succeeded);
    }

    [Fact]
    public async Task FiveFailedAttempts_RateLimitNextLogin()
    {
        await using var fixture = CreateFixture();
        var station = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");

        for (var attempt = 0; attempt < PlayerLoginRateLimiter.MaximumFailuresPerWindow; attempt++)
        {
            var failed = await fixture.Authentication.LoginAsync(
                station.Id,
                "nur",
                "WrongPassword123!",
                null,
                default);
            Assert.Equal(PlayerLoginError.InvalidCredentials, failed.Error);
        }

        var limited = await fixture.Authentication.LoginAsync(
            station.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        Assert.Equal(PlayerLoginError.RateLimited, limited.Error);
        Assert.Contains(
            fixture.Db.SecurityAuditEvents,
            audit => audit.EventType == "PlayerLoginRateLimited" && audit.StationId == station.Id);
    }

    [Fact]
    public void TenRequests_RateLimitNextRequestForStation()
    {
        var limiter = new PlayerLoginRateLimiter();
        var stationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        for (var request = 0; request < PlayerLoginRateLimiter.MaximumRequestsPerWindow; request++)
        {
            Assert.True(limiter.TryBegin(stationId, now));
            limiter.RecordSuccess(stationId, now);
        }

        Assert.False(limiter.TryBegin(stationId, now));
    }

    [Fact]
    public async Task LoginRequest_CannotOverrideAuthenticatedStationIdentity()
    {
        Assert.Null(typeof(PlayerLoginRequest).GetProperty("StationId"));
        await using var fixture = CreateFixture();
        var authenticatedStation = fixture.AddStation("PC-01", "MACHINE-01");
        await fixture.CreateUserAsync("nur", "StrongPassword123!");

        await fixture.Authentication.LoginAsync(
            authenticatedStation.Id,
            "nur",
            "StrongPassword123!",
            null,
            default);

        Assert.Equal(
            authenticatedStation.Id,
            (await fixture.Db.PlayerAuthSessions.SingleAsync()).StationId);
    }

    [Fact]
    public void PublicResponses_DoNotExposePasswordOrPasswordHash()
    {
        var userResponse = new DevelopmentUserResponse(
            Guid.NewGuid(),
            "nur",
            "Nur",
            null,
            null,
            UserStatus.Active,
            DateTime.UtcNow);
        var sessionResponse = new PlayerSessionResponse(
            true,
            Guid.NewGuid(),
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(12),
            new PlayerUserResponse(Guid.NewGuid(), "nur", "Nur"));

        var json = JsonSerializer.Serialize(new { userResponse, sessionResponse });

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", typeof(DevelopmentUserResponse).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Password", typeof(PlayerSessionResponse).GetProperties().Select(p => p.Name));
    }

    private static Fixture CreateFixture(IPasswordHasherService? hasher = null)
    {
        var options = new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new GameClubDbContext(options);
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero));
        var passwordHasher = hasher ?? new AspNetPasswordHasherService(
            new PasswordHasher<User>(Options.Create(new PasswordHasherOptions())));
        var users = new UserProvisioningService(
            db,
            passwordHasher,
            time,
            NullLogger<UserProvisioningService>.Instance);
        var audit = new SecurityAuditService(
            db,
            time,
            NullLogger<SecurityAuditService>.Instance);
        var authentication = new PlayerAuthenticationService(
            db,
            new ClubData(db),
            passwordHasher,
            new PlayerLoginRateLimiter(),
            audit,
            time,
            NullLogger<PlayerAuthenticationService>.Instance);
        return new Fixture(db, time, users, authentication);
    }

    private sealed class Fixture(
        GameClubDbContext db,
        MutableTimeProvider time,
        IUserProvisioningService users,
        IPlayerAuthenticationService authentication) : IAsyncDisposable
    {
        public GameClubDbContext Db { get; } = db;
        public MutableTimeProvider Time { get; } = time;
        public IUserProvisioningService Users { get; } = users;
        public IPlayerAuthenticationService Authentication { get; } = authentication;

        public Station AddStation(string name, string machineName)
        {
            var station = new Station(
                Guid.NewGuid(),
                name,
                machineName,
                "127.0.0.1",
                "0.1.0",
                Time.GetUtcNow().UtcDateTime);
            Db.Stations.Add(station);
            Db.SaveChanges();
            return station;
        }

        public async Task<User> CreateUserAsync(string username, string password)
        {
            var result = await Users.CreateAsync(
                username,
                password,
                username,
                null,
                null,
                default);
            return result.User ?? throw new InvalidOperationException("Test user creation failed.");
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan value) => utcNow += value;
    }

    private sealed class RecordingPasswordHasher(PasswordVerificationResult result) : IPasswordHasherService
    {
        public int VerificationCount { get; private set; }
        public string? VerifiedHash { get; private set; }

        public string HashPassword(User user, string password) => throw new NotSupportedException();

        public PasswordVerificationResult VerifyPassword(User user, string password)
        {
            VerificationCount++;
            VerifiedHash = user.PasswordHash;
            return result;
        }
    }
}
