using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GameClub.Domain.Commands;
using GameClub.Domain.Employees;
using GameClub.Domain.Security;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace GameClub.Server.Tests.Employees;

public sealed class EmployeeSecurityTests
{
    [Fact]
    public void Employee_UsesIndependentIdentityAndCaseInsensitiveUsername()
    {
        var employee = NewEmployee(EmployeeRole.Operator, "  DeskAdmin  ");
        Assert.Equal("DeskAdmin", employee.Username);
        Assert.Equal("DESKADMIN", employee.NormalizedUsername);
        Assert.True(employee.IsActive);
        Assert.NotEqual(Guid.Empty, employee.SecurityStamp);
        Assert.Throws<ArgumentException>(() => NewEmployee(EmployeeRole.Operator, "x"));
    }

    [Fact]
    public void Password_IsHashedAndWrongPasswordFails()
    {
        var employee = NewEmployee();
        var hasher = new PasswordHasher<Employee>();
        employee.SetPasswordHash(hasher.HashPassword(employee, "EmployeeTestPassword!234"));
        Assert.NotEqual("EmployeeTestPassword!234", employee.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(employee, employee.PasswordHash, "EmployeeTestPassword!234"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(employee, employee.PasswordHash, "WrongPassword!234"));
    }

    [Fact]
    public void AccessPasswordAndLogoutChanges_RotateStamp()
    {
        var employee = NewEmployee();
        var old = employee.SecurityStamp;
        employee.ChangeAccess(EmployeeRole.Manager, true);
        Assert.NotEqual(old, employee.SecurityStamp);
        old = employee.SecurityStamp;
        employee.SetPasswordHash("opaque-password-hash");
        Assert.NotEqual(old, employee.SecurityStamp);
        old = employee.SecurityStamp;
        employee.RevokeSessions();
        Assert.NotEqual(old, employee.SecurityStamp);
    }

    [Theory]
    [InlineData(EmployeeRole.Administrator, true, true)]
    [InlineData(EmployeeRole.Manager, true, false)]
    [InlineData(EmployeeRole.Operator, false, false)]
    public void RolePermissionMatrix_SeparatesMoneyAndSecurity(EmployeeRole role, bool money, bool security)
    {
        var permissions = EmployeePermissions.ForRole(role);
        Assert.Contains(EmployeePermissions.Read, permissions);
        Assert.Contains(EmployeePermissions.Operate, permissions);
        Assert.Equal(money, permissions.Contains(EmployeePermissions.Money));
        Assert.Equal(security, permissions.Contains(EmployeePermissions.Security));
        Assert.Equal(security, permissions.Contains(EmployeePermissions.Employees));
    }

    [Fact]
    public void OperatorPower_IsOptInOnly()
    {
        Assert.DoesNotContain(EmployeePermissions.Power, EmployeePermissions.ForRole(EmployeeRole.Operator));
        Assert.Contains(EmployeePermissions.Power, EmployeePermissions.ForRole(EmployeeRole.Operator, true));
        Assert.DoesNotContain(EmployeePermissions.Security, EmployeePermissions.ForRole(EmployeeRole.Operator, true));
    }

    [Fact]
    public void AuthorizationPolicies_ExplicitlyRequireEmployeeJwt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmployeeSecurity(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        foreach (var permission in EmployeePermissions.All)
        {
            var policy = options.GetPolicy(permission);
            Assert.NotNull(policy);
            Assert.Equal([EmployeeAuthenticationDefaults.Scheme], policy.AuthenticationSchemes);
        }
    }

    [Fact]
    public void EmployeeAndAgentJwt_AudiencesCannotBeInterchanged()
    {
        using var keys = new TestSigningKeys();
        var options = Options.Create(new SecurityOptions());
        var employeeToken = new EmployeeTokenService(keys, options, new EmployeeSecurityOptions(), TimeProvider.System).Create(NewEmployee());
        var agentToken = new AgentSessionTokenService(keys, options, TimeProvider.System).Create(Guid.NewGuid());
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var employeeParameters = EmployeeTokenService.ValidationParameters(keys, options.Value);
        var employeePrincipal = handler.ValidateToken(employeeToken.AccessToken, employeeParameters, out _);
        Assert.NotNull(employeePrincipal.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim));
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => handler.ValidateToken(agentToken.AccessToken, employeeParameters, out _));
        var agentParameters = employeeParameters.Clone();
        agentParameters.ValidAudience = "gameclub-agent";
        Assert.Throws<SecurityTokenInvalidAudienceException>(() => handler.ValidateToken(employeeToken.AccessToken, agentParameters, out _));
    }

    [Fact]
    public void Jwt_HasBoundedLifetimeAndContainsNoPassword()
    {
        using var keys = new TestSigningKeys();
        var employee = NewEmployee();
        employee.SetPasswordHash("never-export-this-hash");
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var response = new EmployeeTokenService(keys, Options.Create(new SecurityOptions()), new EmployeeSecurityOptions(), clock).Create(employee);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(4), response.ExpiresAtUtc);
        Assert.DoesNotContain(jwt.Claims, c => c.Type.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(employee.PasswordHash, JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.Equal("Operator", response.Employee.Role);
        employee.ChangeAccess(EmployeeRole.Operator, false);
        Assert.Throws<InvalidOperationException>(() =>
            new EmployeeTokenService(keys, Options.Create(new SecurityOptions()), new EmployeeSecurityOptions(), clock).Create(employee));
    }

    [Fact]
    public void FiveFailedLogins_AreLimitedUntilNextWindow()
    {
        var limiter = new EmployeeLoginLimiter();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryBegin("127.0.0.1", now));
            limiter.Complete("127.0.0.1", false);
        }
        Assert.False(limiter.TryBegin("127.0.0.1", now));
        Assert.True(limiter.TryBegin("127.0.0.2", now));
        Assert.True(limiter.TryBegin("127.0.0.1", now.AddMinutes(1)));
    }

    [Fact]
    public void ConcurrentLoginAttempts_CannotReserveMoreThanRemainingFailureBudget()
    {
        var limiter = new EmployeeLoginLimiter();
        var now = DateTime.UtcNow;
        var accepted = 0;
        Parallel.For(0, 20, _ =>
        {
            if (limiter.TryBegin("127.0.0.1", now)) Interlocked.Increment(ref accepted);
        });
        Assert.Equal(5, accepted);
    }

    [Fact]
    public void SuccessfulLogin_DoesNotEraseFailuresInSameWindow()
    {
        var limiter = new EmployeeLoginLimiter();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            Assert.True(limiter.TryBegin("ip", now));
            limiter.Complete("ip", false);
        }
        Assert.True(limiter.TryBegin("ip", now));
        limiter.Complete("ip", true);
        Assert.True(limiter.TryBegin("ip", now));
        limiter.Complete("ip", false);
        Assert.False(limiter.TryBegin("ip", now));
    }

    [Fact]
    public async Task PrincipalValidation_RejectsMissingInactiveRevokedAndChangedRole()
    {
        await using var db = new GameClubDbContext(new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var employee = NewEmployee();
        db.Set<Employee>().Add(employee);
        await db.SaveChangesAsync();
        var validator = new EmployeePrincipalValidator(db, new EmployeeSecurityOptions());
        var principal = Principal(employee);
        Assert.True(await validator.ValidateAsync(principal, default));
        Assert.False(await validator.ValidateAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("station_id", Guid.NewGuid().ToString())], "AgentJwt")), default));
        employee.RevokeSessions();
        await db.SaveChangesAsync();
        Assert.False(await validator.ValidateAsync(principal, default));
        principal = Principal(employee);
        employee.ChangeAccess(EmployeeRole.Manager, true);
        await db.SaveChangesAsync();
        Assert.False(await validator.ValidateAsync(principal, default));
        principal = Principal(employee);
        employee.ChangeAccess(EmployeeRole.Manager, false);
        await db.SaveChangesAsync();
        Assert.False(await validator.ValidateAsync(principal, default));
    }

    [Fact]
    public async Task PrincipalPermissions_AreRecomputedFromCurrentDatabaseRole()
    {
        await using var db = new GameClubDbContext(new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var employee = NewEmployee();
        db.Set<Employee>().Add(employee);
        await db.SaveChangesAsync();
        var principal = Principal(employee);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("permission", EmployeePermissions.Security));
        Assert.True(await new EmployeePrincipalValidator(db, new EmployeeSecurityOptions()).ValidateAsync(principal, default));
        Assert.DoesNotContain(principal.FindAll("permission"), c => c.Value == EmployeePermissions.Security);
        Assert.Contains(principal.FindAll("permission"), c => c.Value == EmployeePermissions.Read);
    }

    internal static Employee NewEmployee(EmployeeRole role = EmployeeRole.Operator, string username = "operator") =>
        new(Guid.NewGuid(), username, role, DateTime.UtcNow);

    internal static ClaimsPrincipal Principal(Employee employee) => new(new ClaimsIdentity([
        new Claim("employee_id", employee.Id.ToString("D")), new Claim("security_stamp", employee.SecurityStamp.ToString("D")),
        new Claim("role", employee.Role.ToString())], EmployeeAuthenticationDefaults.Scheme));

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class TestSigningKeys : IServerSigningKeyProvider, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public SigningCredentials JwtSigningCredentials => new(new ECDsaSecurityKey(_key), SecurityAlgorithms.EcdsaSha256);
        public SecurityKey JwtValidationKey => new ECDsaSecurityKey(_key);
        public string PublicKeyBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        public SignedAgentCommandEnvelope CreateSignedEnvelope(AgentCommand command) => throw new NotSupportedException();
        public void Dispose() => _key.Dispose();
    }
}
