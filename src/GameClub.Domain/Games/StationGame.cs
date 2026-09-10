namespace GameClub.Domain.Games;

public sealed class StationGame
{
    private StationGame() { }
    public StationGame(Guid stationId, Guid gameId, bool installed, DateTime now)
    {
        if (stationId == Guid.Empty || gameId == Guid.Empty) throw new ArgumentException("Station and game ids are required.");
        StationId = stationId;
        GameId = gameId;
        Report(installed, now);
    }

    public Guid StationId { get; private set; }
    public Guid GameId { get; private set; }
    public bool Installed { get; private set; }
    public DateTime LastDetectedAtUtc { get; private set; }

    public void Report(bool installed, DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc || now < LastDetectedAtUtc)
            throw new ArgumentException("Inventory timestamps must be chronological server UTC.", nameof(now));
        Installed = installed;
        LastDetectedAtUtc = now;
    }
}
