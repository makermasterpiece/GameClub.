using System.ComponentModel.DataAnnotations;

namespace GameClub.Server.Contracts.Stations;

public sealed record HeartbeatRequest(
    [Required, MaxLength(255)] string MachineName,
    [Required, MaxLength(50)] string AgentVersion,
    bool ClientConnected = false,
    [MaxLength(20)] string? ClientState = null,
    DateTime? ClientLastSeenAtUtc = null);
