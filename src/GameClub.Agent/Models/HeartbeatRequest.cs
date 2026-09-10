namespace GameClub.Agent.Models;

public sealed record HeartbeatRequest(string MachineName, string AgentVersion,
    bool ClientConnected = false, string? ClientState = null, DateTime? ClientLastSeenAtUtc = null);
