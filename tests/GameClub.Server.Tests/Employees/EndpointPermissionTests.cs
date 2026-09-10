using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using GameClub.Domain.Employees;
using GameClub.Domain.Users;
using GameClub.Server.Controllers;
using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameClub.Server.Tests.Employees;

public sealed class EndpointPermissionTests
{
    public static IEnumerable<object[]> ProtectedActions()
    {
        yield return Action<CatalogController>(nameof(CatalogController.Get), EmployeePermissions.Read);
        foreach (var method in new[] { nameof(CatalogController.Group), nameof(CatalogController.Tariff),
            nameof(CatalogController.Package), nameof(CatalogController.Price), nameof(CatalogController.TariffActive),
            nameof(CatalogController.PackageActive), nameof(CatalogController.AssignGroup) })
            yield return Action<CatalogController>(method, EmployeePermissions.Catalog);
        yield return Action<WalletController>(nameof(WalletController.Get), EmployeePermissions.Read);
        yield return Action<WalletController>(nameof(WalletController.Transactions), EmployeePermissions.Read);
        yield return Action<WalletController>(nameof(WalletController.Deposit), EmployeePermissions.Operate);
        yield return Action<WalletController>(nameof(WalletController.Adjustment), EmployeePermissions.Money);
        foreach (var method in new[] { nameof(GamingSessionsController.Start), nameof(GamingSessionsController.Pause),
            nameof(GamingSessionsController.Resume), nameof(GamingSessionsController.End),
            nameof(GamingSessionsController.Extend), nameof(GamingSessionsController.Transfer) })
            yield return Action<GamingSessionsController>(method, EmployeePermissions.Operate);
        yield return Action<GamingSessionsController>(nameof(GamingSessionsController.Events), EmployeePermissions.Read);
        yield return Action<GamingSessionsController>(nameof(GamingSessionsController.ExtensionQuote), EmployeePermissions.Read);
        yield return Action<GamingSessionsController>(nameof(GamingSessionsController.Segments), EmployeePermissions.Read);
        yield return Action<StationsController>(nameof(StationsController.GetAll), EmployeePermissions.Read);
        yield return Action<StationsController>(nameof(StationsController.GetById), EmployeePermissions.Read);
        yield return Action<StationsController>(nameof(StationsController.RevokeCredential), EmployeePermissions.Security);
        yield return Action<StationCommandsController>(nameof(StationCommandsController.Create), EmployeePermissions.Operate);
        yield return Action<StationCommandsController>(nameof(StationCommandsController.GetLatest), EmployeePermissions.Read);
        yield return Action<CommandsController>(nameof(CommandsController.GetById), EmployeePermissions.Read);
        yield return Action<AdminEnrollmentTokensController>(nameof(AdminEnrollmentTokensController.Create), EmployeePermissions.Security);
        yield return Action<AdminPlayersController>(nameof(AdminPlayersController.Get), EmployeePermissions.Read);
        yield return Action<AdminPlayersController>(nameof(AdminPlayersController.Create), EmployeePermissions.Operate);
        yield return Action<AdminPlayersController>(nameof(AdminPlayersController.ChangeStatus), EmployeePermissions.Money);
        foreach (var method in new[] { nameof(PosController.Catalog), nameof(PosController.ShiftHistory),
            nameof(PosController.ShiftSummary), nameof(PosController.Sales), nameof(PosController.SaleDetails) })
            yield return Action<PosController>(method, EmployeePermissions.Read);
        foreach (var method in new[] { nameof(PosController.CreateCategory), nameof(PosController.UpdateCategory),
            nameof(PosController.CreateProduct), nameof(PosController.UpdateProduct), nameof(PosController.AdjustStock) })
            yield return Action<PosController>(method, EmployeePermissions.Catalog);
        foreach (var method in new[] { nameof(PosController.CurrentShift), nameof(PosController.OpenShift),
            nameof(PosController.CloseShift), nameof(PosController.Purchase) })
            yield return Action<PosController>(method, EmployeePermissions.Operate);
        yield return Action<PosController>(nameof(PosController.Refund), EmployeePermissions.Money);
    }

    [Theory]
    [MemberData(nameof(ProtectedActions))]
    public void AdministrativeAction_RequiresExplicitEmployeeSchemeAndPermission(Type controller, string method, string permission)
    {
        var action = controller.GetMethod(method)!;
        var attribute = Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(EmployeeAuthenticationDefaults.Scheme, attribute.AuthenticationSchemes);
        Assert.Equal(permission, attribute.Policy);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void EveryOwnedAdministrativeHttpAction_IsIncludedInThePermissionMatrix()
    {
        var expected = ProtectedActions().Select(x => ((Type)x[0], (string)x[1])).ToHashSet();
        var controllers = expected.Select(x => x.Item1).Distinct();
        foreach (var controller in controllers)
        foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                     .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any()))
        {
            if (controller == typeof(StationsController) &&
                action.Name is nameof(StationsController.Enroll) or nameof(StationsController.Register) or nameof(StationsController.Heartbeat))
                continue;
            Assert.Contains((controller, action.Name), expected);
        }
    }

    [Fact]
    public void AgentEndpoints_DoNotAcquireEmployeeAuthenticationRequirements()
    {
        foreach (var method in new[] { nameof(StationsController.Enroll), nameof(StationsController.Register), nameof(StationsController.Heartbeat) })
            Assert.Empty(typeof(StationsController).GetMethod(method)!.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Empty(typeof(StationsController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Empty(typeof(AgentGamingSessionController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Empty(typeof(AgentGamingSessionController).GetMethod(nameof(AgentGamingSessionController.Current))!
            .GetCustomAttributes<AuthorizeAttribute>());
    }

    [Fact]
    public async Task AgentAuthenticatedPrincipal_CannotSatisfyEmployeeEndpointPolicy()
    {
        var fakeAgent = Principal("AgentJwt", EmployeePermissions.All);
        await using var services = CreateServices(employee: null);
        var (result, authentications) = await EvaluateAsync(services, typeof(WalletController),
            nameof(WalletController.Deposit), fakeAgent);

        Assert.True(result.Challenged);
        Assert.Equal(new[] { EmployeeAuthenticationDefaults.Scheme }, authentications);
    }

    [Fact]
    public async Task AnonymousPrincipal_IsChallengedBeforeAdministrativeAction()
    {
        await using var services = CreateServices(employee: null);
        var (result, _) = await EvaluateAsync(services, typeof(AdminEnrollmentTokensController),
            nameof(AdminEnrollmentTokensController.Create), new ClaimsPrincipal());
        Assert.True(result.Challenged);
    }

    [Theory]
    [InlineData(EmployeeRole.Operator, false)]
    [InlineData(EmployeeRole.Manager, true)]
    [InlineData(EmployeeRole.Administrator, true)]
    public async Task BalanceAdjustment_IsRestrictedToManagerOrAdministrator(EmployeeRole role, bool permitted)
    {
        await using var services = CreateServices(Principal(EmployeeAuthenticationDefaults.Scheme, EmployeePermissions.ForRole(role)));
        var (result, _) = await EvaluateAsync(services, typeof(WalletController),
            nameof(WalletController.Adjustment), new ClaimsPrincipal());
        Assert.Equal(permitted, result.Succeeded);
        Assert.Equal(!permitted, result.Forbidden);
    }

    [Theory]
    [InlineData(EmployeeRole.Operator, false)]
    [InlineData(EmployeeRole.Manager, true)]
    [InlineData(EmployeeRole.Administrator, true)]
    public async Task PosRefund_IsRestrictedToManagerOrAdministrator(EmployeeRole role, bool permitted)
    {
        await using var services = CreateServices(Principal(EmployeeAuthenticationDefaults.Scheme, EmployeePermissions.ForRole(role)));
        var (result, _) = await EvaluateAsync(services, typeof(PosController), nameof(PosController.Refund), new ClaimsPrincipal());
        Assert.Equal(permitted, result.Succeeded);
        Assert.Equal(!permitted, result.Forbidden);
    }

    [Fact]
    public async Task AgentPrincipal_CannotPurchaseOrRefundProducts()
    {
        foreach (var method in new[] { nameof(PosController.Purchase), nameof(PosController.Refund) })
        {
            await using var services = CreateServices(employee: null);
            var (result, schemes) = await EvaluateAsync(services, typeof(PosController), method, Principal("AgentJwt", EmployeePermissions.All));
            Assert.True(result.Challenged);
            Assert.Equal(new[] { EmployeeAuthenticationDefaults.Scheme }, schemes);
        }
    }

    [Theory]
    [InlineData(EmployeeRole.Operator, false)]
    [InlineData(EmployeeRole.Manager, false)]
    [InlineData(EmployeeRole.Administrator, true)]
    public async Task EnrollmentTokens_AreRestrictedToAdministrator(EmployeeRole role, bool permitted)
    {
        await using var services = CreateServices(Principal(EmployeeAuthenticationDefaults.Scheme, EmployeePermissions.ForRole(role)));
        var (result, _) = await EvaluateAsync(services, typeof(AdminEnrollmentTokensController),
            nameof(AdminEnrollmentTokensController.Create), new ClaimsPrincipal());
        Assert.Equal(permitted, result.Succeeded);
    }

    [Fact]
    public void PlayerResponse_AndWalletRequest_DoNotExposePasswordOrAcceptEmployeeIdentity()
    {
        var response = new AdminPlayerResponse(Guid.NewGuid(), "nur", "Nur", null, null,
            UserStatus.Active, DateTime.UtcNow, null);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(response), StringComparison.OrdinalIgnoreCase);
        Assert.Null(typeof(WalletChangeRequest).GetProperty("EmployeeId"));
        Assert.Null(typeof(PlayerStatusRequest).GetProperty("EmployeeId"));
    }

    private static object[] Action<T>(string name, string permission) => [typeof(T), name, permission];

    private static ClaimsPrincipal Principal(string scheme, IEnumerable<string> permissions) => new(new ClaimsIdentity(
        permissions.Select(permission => new Claim(EmployeeAuthenticationDefaults.PermissionClaim, permission))
            .Append(new Claim(EmployeeAuthenticationDefaults.EmployeeIdClaim, Guid.NewGuid().ToString("D"))), scheme));

    private static ServiceProvider CreateServices(ClaimsPrincipal? employee)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmployeeSecurity(new ConfigurationBuilder().Build());
        services.AddSingleton<IAuthenticationService>(new SchemeAuthenticationService(employee));
        return services.BuildServiceProvider();
    }

    private static async Task<(PolicyAuthorizationResult Result, List<string?> Schemes)> EvaluateAsync(
        IServiceProvider services, Type controller, string method, ClaimsPrincipal initialPrincipal)
    {
        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(provider,
            controller.GetMethod(method)!.GetCustomAttributes<AuthorizeAttribute>())
            ?? throw new InvalidOperationException("Missing employee policy.");
        Assert.Equal(new[] { EmployeeAuthenticationDefaults.Scheme }, policy.AuthenticationSchemes);
        var evaluator = new PolicyEvaluator(services.GetRequiredService<IAuthorizationService>());
        var context = new DefaultHttpContext { RequestServices = services, User = initialPrincipal };
        var authentication = await evaluator.AuthenticateAsync(policy, context);
        var result = await evaluator.AuthorizeAsync(policy, authentication, context, null);
        return (result, ((SchemeAuthenticationService)services.GetRequiredService<IAuthenticationService>()).Schemes);
    }

    // Substitutes token decoding only. ASP.NET Core's actual policy provider/evaluator still selects
    // the scheme and evaluates the role-derived permissions registered by AddEmployeeSecurity.
    private sealed class SchemeAuthenticationService(ClaimsPrincipal? employee) : IAuthenticationService
    {
        public List<string?> Schemes { get; } = [];
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Schemes.Add(scheme);
            return Task.FromResult(scheme == EmployeeAuthenticationDefaults.Scheme && employee is not null
                ? AuthenticateResult.Success(new AuthenticationTicket(employee, scheme))
                : AuthenticateResult.NoResult());
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
