using GameClub.Domain.Employees;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace GameClub.Server.Security.Employees;

public static class EmployeeSecurityExtensions
{
    public static IServiceCollection AddEmployeeSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new EmployeeSecurityOptions { OperatorCanPower = configuration.GetValue<bool>("Club:OperatorCanPower") });
        services.AddScoped<IPasswordHasher<Employee>, PasswordHasher<Employee>>();
        services.AddSingleton<EmployeeLoginLimiter>();
        services.AddSingleton<EmployeeConnectionRegistry>();
        services.AddHostedService<EmployeeConnectionRevalidator>();
        services.AddScoped<EmployeeTokenService>();
        services.AddScoped<EmployeePrincipalValidator>();
        services.AddScoped<EmployeeAuthenticationService>();
        services.AddScoped<EmployeeManagementService>();
        services.AddAuthentication().AddJwtBearer(EmployeeAuthenticationDefaults.Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(EmployeeAuthenticationDefaults.Scheme)
            .Configure<IServerSigningKeyProvider, IOptions<SecurityOptions>>((options, keys, security) =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = EmployeeTokenService.ValidationParameters(keys, security.Value);
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs/admin") &&
                            context.Request.Query.TryGetValue("access_token", out var token) && token.Count == 1)
                            context.Token = token[0];
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var validator = context.HttpContext.RequestServices.GetRequiredService<EmployeePrincipalValidator>();
                        if (!await validator.ValidateAsync(context.Principal, context.HttpContext.RequestAborted))
                            context.Fail("Employee authorization is no longer valid.");
                    }
                };
            });
        services.AddAuthorization(options =>
        {
            foreach (var permission in EmployeePermissions.All)
                options.AddPolicy(permission, policy => policy.AddAuthenticationSchemes(EmployeeAuthenticationDefaults.Scheme)
                    .RequireAuthenticatedUser().RequireClaim(EmployeeAuthenticationDefaults.PermissionClaim, permission));
        });
        return services;
    }

    /// <summary>Only invoke from the explicit --bootstrap-admin CLI branch, never from an HTTP endpoint or normal startup.</summary>
    public static async Task<EmployeeAdminResponse> BootstrapFirstAdministratorAsync(this IServiceProvider services,
        IConfiguration configuration, CancellationToken ct = default)
    {
        // Configuration files/command-line arguments must never become persistent bootstrap credentials.
        var username = Environment.GetEnvironmentVariable("Bootstrap__Username");
        var password = Environment.GetEnvironmentVariable("Bootstrap__Password");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Bootstrap__Username and Bootstrap__Password must be supplied explicitly.");
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EmployeeManagementService>().BootstrapAsync(username, password, ct);
    }
}
