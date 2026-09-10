using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using GameClub.Application.Abstractions;
using GameClub.Domain.Employees;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using GameClub.Server.Security.Employees;
using GameClub.Server.Tests.Integration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Employees;

[Collection(PostgresCollection.Name)]
public sealed class EmployeeIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "EmployeeOnlyTest!234";

    [PostgresFact]
    public async Task Bootstrap_IsOneTimeAndStoresOnlyPasswordHashWithAudit()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var management = Management(db);
        var created = await management.BootstrapAsync("  Admin  ", Password, default);
        Assert.Equal("Administrator", created.Role);
        Assert.Equal("Admin", created.Username);
        var repeat = await Assert.ThrowsAsync<ClubException>(() => management.BootstrapAsync("otheradmin", Password, default));
        Assert.Equal("BOOTSTRAP_ALREADY_COMPLETED", repeat.Code);
        await using var verify = database.CreateContext();
        var employee = await verify.Set<Employee>().SingleAsync();
        Assert.Equal("ADMIN", employee.NormalizedUsername);
        Assert.NotEqual(Password, employee.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<Employee>().VerifyHashedPassword(employee, employee.PasswordHash, Password));
        var audit = await verify.SecurityAuditEvents.SingleAsync();
        Assert.Equal("EmployeeBootstrapped", audit.EventType);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(audit), StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordHash", JsonSerializer.Serialize(created), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ConcurrentBootstrap_CreatesExactlyOneAdministrator()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => Management(firstDb).BootstrapAsync("first-admin", Password, default)),
            Record.ExceptionAsync(() => Management(secondDb).BootstrapAsync("second-admin", Password, default)));
        Assert.Single(outcomes, result => result is null);
        var failure = Assert.Single(outcomes, result => result is not null);
        Assert.True(failure is ClubException, $"Unexpected bootstrap failure: {failure}");
        Assert.Equal("BOOTSTRAP_ALREADY_COMPLETED", ((ClubException)failure!).Code);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<Employee>().CountAsync());
        Assert.Equal(1, await verify.SecurityAuditEvents.CountAsync(e => e.EventType == "EmployeeBootstrapped"));
    }

    [PostgresFact]
    public async Task Bootstrap_RetriesSerializationFailureAtCommitWithoutMaskingIt()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        // Deferred triggers execute during COMMIT, after SaveChanges succeeded. PostgreSQL
        // sequences are not rolled back, so exactly the first commit raises SQLSTATE 40001.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SEQUENCE gameclub_test_commit_attempt;
            CREATE FUNCTION gameclub_test_fail_first_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('gameclub_test_commit_attempt') = 1 THEN
                    RAISE EXCEPTION 'Synthetic serialization conflict at commit' USING ERRCODE = '40001';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE CONSTRAINT TRIGGER gameclub_test_deferred_commit_failure
            AFTER INSERT ON employees DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION gameclub_test_fail_first_commit();
            """);

        var created = await Management(db).BootstrapAsync("commit-retry-admin", Password, default);

        await using var verify = database.CreateContext();
        Assert.Equal(created.Id, (await verify.Set<Employee>().SingleAsync()).Id);
        Assert.Equal(1, await verify.SecurityAuditEvents.CountAsync(e => e.EventType == "EmployeeBootstrapped"));
        var attempts = await verify.Database.SqlQueryRaw<long>(
            "SELECT last_value AS \"Value\" FROM gameclub_test_commit_attempt").SingleAsync();
        Assert.Equal(2, attempts);
    }

    [PostgresFact]
    public async Task EmployeeCreate_RejectsCaseInsensitiveDuplicatesAndNonAdminActors()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var management = Management(db);
        var admin = await management.BootstrapAsync("admin", Password, default);
        var manager = await management.CreateAsync(admin.Id, "DeskManager", Password, EmployeeRole.Manager, default);
        var duplicate = await Assert.ThrowsAsync<ClubException>(() =>
            management.CreateAsync(admin.Id, "DESKMANAGER", Password, EmployeeRole.Operator, default));
        Assert.Equal("EMPLOYEE_USERNAME_EXISTS", duplicate.Code);
        var unauthorized = await Assert.ThrowsAsync<ClubException>(() =>
            management.CreateAsync(manager.Id, "evil-admin", Password, EmployeeRole.Administrator, default));
        Assert.Equal("EMPLOYEE_PERMISSION_DENIED", unauthorized.Code);
        await using var verify = database.CreateContext();
        Assert.Equal(2, await verify.Set<Employee>().CountAsync());
    }

    [PostgresFact]
    public async Task Login_UsesSameFailureForUnknownWrongPasswordAndInactiveEmployee()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var management = Management(db);
        var admin = await management.BootstrapAsync("admin", Password, default);
        var employee = await management.CreateAsync(admin.Id, "operator", Password, EmployeeRole.Operator, default);
        using var keys = new EmployeeSecurityTests.TestSigningKeys();
        var auth = Authentication(db, keys);
        var unknown = await auth.LoginAsync("missing", Password, "127.0.0.1", default);
        var wrong = await auth.LoginAsync("operator", "WrongPassword!234", "127.0.0.1", default);
        await management.ChangeAccessAsync(admin.Id, employee.Id, EmployeeRole.Operator, false, default);
        var inactive = await auth.LoginAsync("operator", Password, "127.0.0.1", default);
        Assert.Equal("INVALID_CREDENTIALS", unknown.ErrorCode);
        Assert.Equal(unknown.ErrorCode, wrong.ErrorCode);
        Assert.Equal(unknown.ErrorCode, inactive.ErrorCode);
        Assert.Null(unknown.Response);
        Assert.Null(wrong.Response);
        Assert.Null(inactive.Response);
        var good = await auth.LoginAsync("ADMIN", Password, "127.0.0.1", default);
        Assert.NotNull(good.Response);
        Assert.Contains(EmployeePermissions.Security, good.Response.Employee.Permissions);
        Assert.All(await db.SecurityAuditEvents.ToListAsync(), e => Assert.DoesNotContain(Password, e.Details ?? string.Empty, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task UnknownEmployee_StillRunsPasswordVerification()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        using var keys = new EmployeeSecurityTests.TestSigningKeys();
        var hasher = new RecordingHasher();
        var auth = Authentication(db, keys, hasher);
        var result = await auth.LoginAsync("unknown", Password, "127.0.0.1", default);
        Assert.Equal("INVALID_CREDENTIALS", result.ErrorCode);
        Assert.Equal(1, hasher.Verifications);
        Assert.False(string.IsNullOrWhiteSpace(hasher.VerifiedHash));
        Assert.Empty(await db.Set<Employee>().ToListAsync());
    }

    [PostgresFact]
    public async Task LogoutAndPasswordChange_InvalidateIssuedEmployeeTokens()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var management = Management(db);
        var admin = await management.BootstrapAsync("admin", Password, default);
        using var keys = new EmployeeSecurityTests.TestSigningKeys();
        var auth = Authentication(db, keys);
        var first = await auth.LoginAsync("admin", Password, "127.0.0.1", default);
        var principal = ReadPrincipal(first.Response!.AccessToken);
        var validator = new EmployeePrincipalValidator(db, new EmployeeSecurityOptions());
        Assert.True(await validator.ValidateAsync(principal, default));
        await auth.LogoutAsync(admin.Id, "127.0.0.1", default);
        Assert.False(await validator.ValidateAsync(principal, default));
        var second = await auth.LoginAsync("admin", Password, "127.0.0.1", default);
        var secondPrincipal = ReadPrincipal(second.Response!.AccessToken);
        await management.ChangePasswordAsync(admin.Id, admin.Id, "ChangedEmployeeTest!567", default);
        Assert.False(await validator.ValidateAsync(secondPrincipal, default));
        Assert.Equal("INVALID_CREDENTIALS", (await auth.LoginAsync("admin", Password, "127.0.0.1", default)).ErrorCode);
        Assert.NotNull((await auth.LoginAsync("admin", "ChangedEmployeeTest!567", "127.0.0.1", default)).Response);
    }

    [PostgresFact]
    public async Task LastActiveAdministrator_CannotBeDisabledOrDemoted()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var management = Management(db);
        var admin = await management.BootstrapAsync("admin", Password, default);
        Assert.Equal("LAST_ADMINISTRATOR_REQUIRED", (await Assert.ThrowsAsync<ClubException>(() =>
            management.ChangeAccessAsync(admin.Id, admin.Id, EmployeeRole.Administrator, false, default))).Code);
        Assert.Equal("LAST_ADMINISTRATOR_REQUIRED", (await Assert.ThrowsAsync<ClubException>(() =>
            management.ChangeAccessAsync(admin.Id, admin.Id, EmployeeRole.Operator, true, default))).Code);
        var backup = await management.CreateAsync(admin.Id, "backup-admin", Password, EmployeeRole.Administrator, default);
        await management.ChangeAccessAsync(backup.Id, admin.Id, EmployeeRole.Operator, false, default);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<Employee>().CountAsync(e => e.Role == EmployeeRole.Administrator && e.IsActive));
    }

    private static EmployeeManagementService Management(GameClubDbContext db) =>
        new(new ClubData(db), new PasswordHasher<Employee>(), TimeProvider.System);

    private static EmployeeAuthenticationService Authentication(GameClubDbContext db,
        EmployeeSecurityTests.TestSigningKeys keys, IPasswordHasher<Employee>? hasher = null) =>
        new(new ClubData(db), hasher ?? new PasswordHasher<Employee>(),
            new EmployeeTokenService(keys, Options.Create(new SecurityOptions()), new EmployeeSecurityOptions(), TimeProvider.System),
            new EmployeeLoginLimiter(), TimeProvider.System);

    private static ClaimsPrincipal ReadPrincipal(string token) => new(new ClaimsIdentity(
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, EmployeeAuthenticationDefaults.Scheme));

    private sealed class RecordingHasher : IPasswordHasher<Employee>
    {
        public int Verifications { get; private set; }
        public string? VerifiedHash { get; private set; }
        public string HashPassword(Employee user, string password) => throw new NotSupportedException();
        public PasswordVerificationResult VerifyHashedPassword(Employee user, string hashedPassword, string providedPassword)
        {
            Verifications++;
            VerifiedHash = hashedPassword;
            return PasswordVerificationResult.Failed;
        }
    }
}
