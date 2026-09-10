namespace GameClub.Contracts.Gaming;

public sealed record GamingSessionSnapshot(
    Guid Id,
    Guid UserId,
    Guid StationId,
    string Status,
    DateTime? StartedAtUtc,
    DateTime? ExpectedEndAtUtc,
    int? RemainingSeconds,
    DateTime ServerTimeUtc,
    long ElapsedSeconds,
    Guid? TariffId = null,
    Guid? PackageId = null);

public sealed record StationGamingState(
    GamingSessionSnapshot? Session,
    DateTime ServerTimeUtc);
