namespace GameClub.Server.Services.Commands;

public sealed class StationConnectionRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, HashSet<string>> _connections = [];

    public void Add(Guid stationId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(stationId, out var stationConnections))
            {
                stationConnections = [];
                _connections[stationId] = stationConnections;
            }

            stationConnections.Add(connectionId);
        }
    }

    public void Remove(Guid stationId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(stationId, out var stationConnections))
            {
                return;
            }

            stationConnections.Remove(connectionId);
            if (stationConnections.Count == 0)
            {
                _connections.Remove(stationId);
            }
        }
    }

    public bool IsConnected(Guid stationId)
    {
        lock (_lock)
        {
            return _connections.TryGetValue(stationId, out var connections) && connections.Count > 0;
        }
    }
}
