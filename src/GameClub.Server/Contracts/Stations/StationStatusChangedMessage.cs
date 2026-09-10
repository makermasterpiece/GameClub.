using GameClub.Domain.Stations;

namespace GameClub.Server.Contracts.Stations;

public sealed record StationStatusChangedMessage(
    Guid StationId,
    string Name,
    StationStatus Status,
    DateTime LastSeenAtUtc);
