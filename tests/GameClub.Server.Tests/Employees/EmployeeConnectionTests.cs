using System.Globalization;
using System.Security.Claims;
using GameClub.Domain.Employees;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameClub.Server.Tests.Employees;

public sealed class EmployeeConnectionTests
{
    [Fact]
    public void Registry_CopiesImmutableIdentityAndDoesNotRetainMutablePrincipal()
    {
        var employee = EmployeeSecurityTests.NewEmployee();
        var principal = Principal(employee);
        var registry = new EmployeeConnectionRegistry();
        Assert.True(registry.TryRegister("connection", principal, () => { }));
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("role")!);
        identity.AddClaim(new Claim("role", "Administrator"));
        var saved = Assert.Single(registry.Snapshot());
        Assert.Equal("Operator", saved.Identity.Role);
        Assert.Equal(employee.Id, saved.Identity.EmployeeId);
        Assert.Equal(employee.SecurityStamp, saved.Identity.SecurityStamp);
        Assert.DoesNotContain(saved.Identity.CreatePrincipal().Claims, c => c.Type == "access_token");
    }

    [Fact]
    public void Registry_RejectsMissingExpiryAndAgentPrincipal()
    {
        var registry = new EmployeeConnectionRegistry();
        var employee = EmployeeSecurityTests.NewEmployee();
        Assert.False(registry.TryRegister("missing-expiry", EmployeeSecurityTests.Principal(employee), () => { }));
        Assert.False(registry.TryRegister("agent", new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("station_id", Guid.NewGuid().ToString("D"))], "AgentJwt")), () => { }));
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void DisconnectedConnection_IsRemovedWithoutCallingAbort()
    {
        var registry = new EmployeeConnectionRegistry();
        var aborted = 0;
        registry.TryRegister("connection", Principal(EmployeeSecurityTests.NewEmployee()), () => aborted++);
        registry.Remove("connection");
        Assert.Empty(registry.Snapshot());
        Assert.Equal(0, aborted);
    }

    [Fact]
    public void StaleSnapshot_CannotAbortReusedConnectionId()
    {
        var registry = new EmployeeConnectionRegistry();
        var oldAborts = 0;
        var newAborts = 0;
        var principal = Principal(EmployeeSecurityTests.NewEmployee());
        registry.TryRegister("same-id", principal, () => oldAborts++);
        var old = Assert.Single(registry.Snapshot());
        registry.Remove("same-id");
        registry.TryRegister("same-id", principal, () => newAborts++);
        Assert.False(registry.TryAbort(old));
        Assert.Equal(0, oldAborts);
        Assert.Equal(0, newAborts);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Abort_IsIdempotentAcrossParallelRevalidationAndDisconnect()
    {
        var registry = new EmployeeConnectionRegistry();
        var aborts = 0;
        registry.TryRegister("connection", Principal(EmployeeSecurityTests.NewEmployee()), () => Interlocked.Increment(ref aborts));
        var registration = Assert.Single(registry.Snapshot());
        Parallel.For(0, 10, _ => registry.TryAbort(registration));
        Assert.Equal(1, aborts);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task Revalidator_LeavesValidConnectionsAndAbortsRevokedStamp()
    {
        await using var provider = CreateProvider();
        var employee = EmployeeSecurityTests.NewEmployee();
        await SeedAsync(provider, employee);
        var registry = new EmployeeConnectionRegistry();
        var aborts = 0;
        registry.TryRegister("connection", Principal(employee), () => aborts++);
        var service = Revalidator(provider, registry);
        await service.RevalidateOnceAsync(default);
        Assert.Equal(0, aborts);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameClubDbContext>();
            (await db.Set<Employee>().SingleAsync()).RevokeSessions();
            await db.SaveChangesAsync();
        }
        await service.RevalidateOnceAsync(default);
        Assert.Equal(1, aborts);
        Assert.Empty(registry.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revalidator_AbortsDisabledOrChangedRole(bool changeRole)
    {
        await using var provider = CreateProvider();
        var employee = EmployeeSecurityTests.NewEmployee();
        await SeedAsync(provider, employee);
        var registry = new EmployeeConnectionRegistry();
        var aborts = 0;
        registry.TryRegister("connection", Principal(employee), () => aborts++);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameClubDbContext>();
            var persisted = await db.Set<Employee>().SingleAsync();
            persisted.ChangeAccess(changeRole ? EmployeeRole.Manager : EmployeeRole.Operator, changeRole);
            await db.SaveChangesAsync();
        }
        await Revalidator(provider, registry).RevalidateOnceAsync(default);
        Assert.Equal(1, aborts);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task Revalidator_AbortsExpiredTokenWithoutRequiringDatabase()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var registry = new EmployeeConnectionRegistry();
        var aborts = 0;
        registry.TryRegister("expired", Principal(EmployeeSecurityTests.NewEmployee(), DateTimeOffset.UtcNow.AddMinutes(-1)), () => aborts++);
        await Revalidator(provider, registry).RevalidateOnceAsync(default);
        Assert.Equal(1, aborts);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task Revalidator_FailsClosedWhenAuthorizationCannotBeResolved()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var registry = new EmployeeConnectionRegistry();
        var aborts = 0;
        registry.TryRegister("connection", Principal(EmployeeSecurityTests.NewEmployee()), () => aborts++);
        await Revalidator(provider, registry).RevalidateOnceAsync(default);
        Assert.Equal(1, aborts);
        Assert.Empty(registry.Snapshot());
    }

    private static ClaimsPrincipal Principal(Employee employee, DateTimeOffset? expires = null)
    {
        var principal = EmployeeSecurityTests.Principal(employee);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("exp", (expires ?? DateTimeOffset.UtcNow.AddHours(4))
            .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        return principal;
    }

    private static ServiceProvider CreateProvider()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<GameClubDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(new EmployeeSecurityOptions());
        services.AddScoped<EmployeePrincipalValidator>();
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(ServiceProvider provider, Employee employee)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameClubDbContext>();
        db.Set<Employee>().Add(employee);
        await db.SaveChangesAsync();
    }

    private static EmployeeConnectionRevalidator Revalidator(ServiceProvider provider, EmployeeConnectionRegistry registry) =>
        new(registry, provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<EmployeeConnectionRevalidator>.Instance);
}
