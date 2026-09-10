using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using GameClub.Server.Services.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Hubs;

[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class StationsHub(
    GameClubDbContext dbContext,
    StationConnectionRegistry connectionRegistry,
    IAgentCommandService commandService,
    ILogger<StationsHub> logger) : Hub
{
    private const string AuthenticatedStationItem = "AuthenticatedStationId";

    public override async Task OnConnectedAsync()
    {
        var stationIdValue = Context.User?.FindFirst("station_id")?.Value;
        if (!Guid.TryParse(stationIdValue, out var stationId) ||
            !await dbContext.StationCredentials.AsNoTracking().AnyAsync(
                credential => credential.StationId == stationId && credential.RevokedAtUtc == null,
                Context.ConnectionAborted))
        {
            logger.LogWarning(
                "Rejected unauthenticated station SignalR connection {ConnectionId}",
                Context.ConnectionId);
            Context.Abort();
            throw new HubException("Station authentication failed.");
        }

        Context.Items[AuthenticatedStationItem] = stationId;
        connectionRegistry.Add(stationId, Context.ConnectionId);
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GetStationGroupName(stationId),
            Context.ConnectionAborted);

        logger.LogInformation(
            "Station {StationId} connected to SignalR as {ConnectionId}",
            stationId,
            Context.ConnectionId);

        await base.OnConnectedAsync();
        await commandService.DispatchPendingCommandsAsync(stationId, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetAuthenticatedStationId(out var stationId))
        {
            connectionRegistry.Remove(stationId, Context.ConnectionId);
            logger.LogInformation(
                "Station {StationId} disconnected from SignalR connection {ConnectionId}",
                stationId,
                Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task AcknowledgeCommand(Guid commandId)
    {
        var stationId = await GetAuthenticatedStationIdAsync();
        var result = await commandService.AcknowledgeAsync(
            commandId,
            stationId,
            Context.ConnectionAborted);
        EnsureAccepted(result);
    }

    public async Task CompleteCommand(Guid commandId)
    {
        var stationId = await GetAuthenticatedStationIdAsync();
        var result = await commandService.CompleteAsync(
            commandId,
            stationId,
            Context.ConnectionAborted);
        EnsureAccepted(result);
    }

    public async Task FailCommand(Guid commandId, string? error)
    {
        var stationId = await GetAuthenticatedStationIdAsync();
        var result = await commandService.FailAsync(
            commandId,
            stationId,
            error,
            Context.ConnectionAborted);
        EnsureAccepted(result);
    }

    public static string GetStationGroupName(Guid stationId) => $"station:{stationId:D}";

    private async Task<Guid> GetAuthenticatedStationIdAsync()
    {
        if (TryGetAuthenticatedStationId(out var stationId) &&
            await dbContext.StationCredentials.AsNoTracking().AnyAsync(
                credential => credential.StationId == stationId && credential.RevokedAtUtc == null,
                Context.ConnectionAborted))
        {
            return stationId;
        }

        throw new HubException("Station connection is not authenticated.");
    }

    private bool TryGetAuthenticatedStationId(out Guid stationId)
    {
        if (Context.Items.TryGetValue(AuthenticatedStationItem, out var value) && value is Guid id)
        {
            stationId = id;
            return true;
        }

        stationId = Guid.Empty;
        return false;
    }

    private static void EnsureAccepted(CommandOperationResult result)
    {
        if (result != CommandOperationResult.Success)
        {
            throw new HubException("Command transition was rejected.");
        }
    }
}
