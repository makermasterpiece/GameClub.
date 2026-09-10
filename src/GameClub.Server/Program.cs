using System.Text.Json.Serialization;
using GameClub.Infrastructure;
using GameClub.Server.Hubs;
using GameClub.Server.Security;
using GameClub.Server.Services;
using GameClub.Server.Services.Commands;
using GameClub.Domain.Security;
using GameClub.Infrastructure.Persistence;
using GameClub.Domain.Users;
using GameClub.Server.Endpoints;
using GameClub.Server.Services.Players;
using GameClub.Application.Abstractions;
using GameClub.Application.Gaming;
using GameClub.Application.Billing;
using GameClub.Application.Stations;
using GameClub.Application.Pos;
using GameClub.Server.Security.Employees;
using GameClub.Server.Services.Admin;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var bootstrapAdministrator = args.Contains("--bootstrap-admin", StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(args.Where(arg => !string.Equals(arg, "--bootstrap-admin", StringComparison.OrdinalIgnoreCase)).ToArray());
builder.Services.AddScoped<ProductCatalogService>();
builder.Services.AddScoped<PosSaleService>();
builder.Services.AddScoped<ShiftService>();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services
    .AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("GameClub.Server");
var dataProtectionKeysPath = builder.Configuration["Security:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    var resolvedDataProtectionKeysPath = Path.GetFullPath(dataProtectionKeysPath);
    Directory.CreateDirectory(resolvedDataProtectionKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(resolvedDataProtectionKeysPath));
    if (OperatingSystem.IsWindows())
    {
        dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
    }
}
builder.Services.Configure<SecurityOptions>(
    builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddSingleton<IStationTokenService, StationTokenService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReplayProtectionStore, InMemoryReplayProtectionStore>();
builder.Services.AddSingleton<IServerSigningKeyProvider, ServerSigningKeyProvider>();
builder.Services.AddSingleton<IServerCommandSigner>(provider =>
    provider.GetRequiredService<IServerSigningKeyProvider>());
builder.Services.AddScoped<IStationSecretProtector, StationSecretProtector>();
builder.Services.AddScoped<ISecurityAuditService, SecurityAuditService>();
builder.Services.AddScoped<IStationHmacAuthenticator, StationHmacAuthenticator>();
builder.Services.AddScoped<IStationEnrollmentService, StationEnrollmentService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IPasswordHasherService, AspNetPasswordHasherService>();
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();
builder.Services.AddSingleton<IPlayerLoginRateLimiter, PlayerLoginRateLimiter>();
builder.Services.AddScoped<IPlayerAuthenticationService, PlayerAuthenticationService>();
builder.Services.AddSingleton<IAgentSessionTokenService, AgentSessionTokenService>();
builder.Services.AddSingleton<StationConnectionRegistry>();
builder.Services.AddSingleton<IStationCommandTransport, SignalRStationCommandTransport>();
builder.Services.AddScoped<IAgentCommandService, AgentCommandService>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<StationStatusMonitor>();
builder.Services.AddScoped<GamingSessionService>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<SessionBillingService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<GameClub.Application.Games.GameCatalogService>();
builder.Services.AddScoped<GameClub.Application.Games.GameLaunchService>();
builder.Services.AddScoped<StationManagementService>();
builder.Services.AddSingleton(new WalletPolicy
{
    AllowNegativeBalance = builder.Configuration.GetValue<bool>("Club:AllowNegativeBalance")
});
builder.Services.AddSingleton(new SessionBillingOptions { ClubTimeZoneId = builder.Configuration["Club:TimeZoneId"] ?? "UTC" });
builder.Services.AddSingleton<IClubEvents, ClubEvents>();
builder.Services.AddHostedService<GamingSessionMonitor>();

builder.Services
    .AddAuthentication()
    .AddJwtBearer("AgentJwt", _ => { });
builder.Services.AddOptions<JwtBearerOptions>("AgentJwt")
    .Configure<IServerSigningKeyProvider, Microsoft.Extensions.Options.IOptions<SecurityOptions>>(
        (options, keys, securityOptions) =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = keys.JwtValidationKey,
                ValidateIssuer = true,
                ValidIssuer = securityOptions.Value.JwtIssuer,
                ValidateAudience = true,
                ValidAudience = "gameclub-agent",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(15),
                NameClaimType = "station_id"
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var stationClaim = context.Principal?.FindFirst("station_id")?.Value;
                    var dbContext = context.HttpContext.RequestServices
                        .GetRequiredService<GameClubDbContext>();
                    if (!Guid.TryParse(stationClaim, out var stationId) ||
                        !await dbContext.StationCredentials.AsNoTracking().AnyAsync(
                            credential => credential.StationId == stationId &&
                                          credential.RevokedAtUtc == null,
                            context.HttpContext.RequestAborted))
                    {
                        context.Fail("Station credential is not active.");
                    }
                }
            };
        });
builder.Services.AddAuthorization();
builder.Services.AddEmployeeSecurity(builder.Configuration);
builder.Services.AddScoped<DashboardService>();
builder.Services.AddHostedService<DashboardBroadcaster>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("enrollment", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("agent-session", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Headers[HmacRequestAuthentication.StationIdHeader].ToString(),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("station-commands", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Request.RouteValues["stationId"]?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

// Resolve eagerly: production startup must fail before listening if no signing key is configured.
_ = app.Services.GetRequiredService<IServerSigningKeyProvider>();

if (bootstrapAdministrator)
{
    var administrator = await app.Services.BootstrapFirstAdministratorAsync(builder.Configuration);
    app.Logger.LogInformation("First administrator created: {EmployeeId}. Bootstrap process is exiting", administrator.Id);
    return;
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (ClubException ex)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { code = ex.Code });
    }
    catch (ArgumentException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { code = "INVALID_ARGUMENT" });
    }
});
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
if (app.Environment.IsDevelopment())
{
    app.MapDevelopmentUserEndpoint();
}
app.MapHub<StationsHub>("/hubs/stations", options => options.CloseOnAuthenticationExpiration = true);
app.MapHub<AdminHub>("/hubs/admin", options => options.CloseOnAuthenticationExpiration = true);

var adminDirectory = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "admin");
if (!Directory.Exists(adminDirectory))
    adminDirectory = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "GameClub.Admin", "dist"));
if (Directory.Exists(adminDirectory))
{
    var files = new PhysicalFileProvider(adminDirectory);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = "/admin" });
    app.MapGet("/admin/", () => Results.File(Path.Combine(adminDirectory, "index.html"), "text/html"));
    app.MapFallbackToFile("/admin/{*path:nonfile}", "index.html", new StaticFileOptions { FileProvider = files });
    app.MapGet("/", () => Results.Redirect("/admin/"));
}

app.Run();

public partial class Program;
