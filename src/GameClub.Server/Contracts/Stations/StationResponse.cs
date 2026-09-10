using GameClub.Domain.Stations;

namespace GameClub.Server.Contracts.Stations;

public sealed record StationResponse(
    Guid Id,
    string Name,
    string MachineName,
    string? IpAddress,
    string AgentVersion,
    StationStatus Status,
    DateTime CreatedAtUtc,
    DateTime LastSeenAtUtc,
    Guid? CurrentUserId,
    string? CurrentUsername,
    Guid? PlayerAuthSessionId);
