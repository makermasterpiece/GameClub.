using GameClub.Contracts.Gaming;

namespace GameClub.Contracts.Client;

public sealed record ClientStateMessage(
    ClientShellState State,
    string StationName,
    DateTime TimestampUtc,
    string? Message,
    long Revision,
    Guid? UserId = null,
    string? Username = null,
    string? DisplayName = null,
    Guid? PlayerAuthSessionId = null,
    GamingSessionSnapshot? GamingSession = null);
