using GameClub.Server.Security.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameClub.Server.Hubs;

[Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
public sealed class AdminHub(EmployeeConnectionRegistry connections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var connection = Context;
        if (!connections.TryRegister(connection.ConnectionId, connection.User, connection.Abort))
        {
            connection.Abort();
            return;
        }

        try { await base.OnConnectedAsync(); }
        catch
        {
            connections.Remove(connection.ConnectionId);
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
