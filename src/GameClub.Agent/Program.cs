using GameClub.Agent;
using GameClub.Agent.Configuration;
using GameClub.Agent.Services;
using GameClub.Agent.Services.Commands;
using GameClub.Agent.Security;
using GameClub.Agent.Services.Client;
using GameClub.Agent.Services.Players;
using GameClub.Agent.Services.Games;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "GameClub Agent");
}

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
builder.Services.Configure<StationOptions>(builder.Configuration.GetSection(StationOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IStationCredentialStore, DpapiStationCredentialStore>();
builder.Services.AddSingleton<IServerCommandSignatureVerifier, ServerCommandSignatureVerifier>();
builder.Services.AddSingleton<IProcessedCommandStore, SqliteProcessedCommandStore>();
builder.Services.AddSingleton<IClientStateStore, SqliteClientStateStore>();
builder.Services.AddSingleton<IPlayerSessionStateStore, SqlitePlayerSessionStateStore>();
builder.Services.AddSingleton<ClientStateCoordinator>();
builder.Services.AddSingleton<IClientStateCoordinator>(provider =>
    provider.GetRequiredService<ClientStateCoordinator>());
builder.Services.AddSingleton<IClientPipeServerFactory, WindowsClientPipeServerFactory>();
builder.Services.AddSingleton<ClientPipeServer>();
builder.Services.AddSingleton<IClientStateNotifier>(provider =>
    provider.GetRequiredService<ClientPipeServer>());
builder.Services.AddSingleton<IPlayerLoginService, PlayerLoginService>();
builder.Services.AddSingleton<ILocalPlayniteLibrary, LocalPlayniteLibrary>();
builder.Services.AddSingleton<IAgentGameLaunchService, AgentGameLaunchService>();
builder.Services.AddSingleton<IPlayniteActionTransport>(provider => provider.GetRequiredService<ClientPipeServer>());
builder.Services.AddHostedService<GameInventoryWorker>();
builder.Services.AddSingleton<StationSessionSyncSignal>();
builder.Services.AddSingleton<IWindowsSystemPowerApi, WindowsSystemPowerApi>();
builder.Services.AddSingleton<ISystemPowerService, WindowsSystemPowerService>();
builder.Services.AddSingleton<RestartStationCommandHandler>();
builder.Services.AddSingleton<ShutdownStationCommandHandler>();
builder.Services.AddSingleton<IAgentCommandHandler, AgentCommandHandler>();
builder.Services.AddSingleton<AgentSignalRClient>();
builder.Services.AddHttpClient<StationApiClient>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ClientPipeServer>());

var host = builder.Build();
try
{
    ServerEndpointPolicy.Validate(
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value,
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value,
        host.Services.GetRequiredService<IHostEnvironment>());
}
catch (InvalidOperationException exception)
{
    host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("GameClub.Agent.Security")
        .LogCritical(exception, "Agent network security policy rejected Server:BaseUrl");
    throw;
}
host.Run();
