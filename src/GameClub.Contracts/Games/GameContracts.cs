namespace GameClub.Contracts.Games;

public sealed record StationGameInventoryEntry(Guid GameId, Guid PlayniteGameId, bool Installed);
public sealed record StationGameInventoryRequest(IReadOnlyList<StationGameInventoryEntry> Games);
public sealed record GameAuthorizationRequest(Guid GamingSessionId, Guid? GameId = null);
public sealed record GameAuthorizationResponse(
    Guid GamingSessionId, Guid? GameId, Guid? PlayniteGameId, DateTime ExpiresAtUtc);
public sealed record LaunchGamePayload(Guid GameId, Guid GamingSessionId, Guid PlayniteGameId);

public sealed record GamesMenuRequest(Guid RequestId);
public sealed record GamesMenuResult(Guid RequestId, bool Success, string? ErrorCode);

public enum PlayniteActionType { OpenFullscreen, LaunchGame }

// No executable, working directory, URI, or free-form command-line crosses IPC.
public sealed record PlayniteActionRequest(
    Guid RequestId, PlayniteActionType Action, Guid GamingSessionId,
    DateTime ExpiresAtUtc, Guid? GameId = null, Guid? PlayniteGameId = null);
public sealed record PlayniteActionResult(Guid RequestId, bool Success, string? ErrorCode);

public sealed record PlayniteAllowedGame(Guid GameId, Guid PlayniteGameId);
public sealed record PlayniteLocalConfiguration(
    string FullscreenExecutable, string LibraryManifestPath,
    bool CloseOnSessionEnd, IReadOnlyList<PlayniteAllowedGame> AllowedGames);
public sealed record PlayniteLibraryGame(Guid Id, string Name, bool IsInstalled);
public sealed record PlayniteLibraryManifest(int SchemaVersion, DateTime ExportedAtUtc, IReadOnlyList<PlayniteLibraryGame> Games);

public static class PlaynitePolicy
{
    public const int MaximumGames = 500;
    public const int MaximumConfigurationBytes = 128 * 1024;
    public const int MaximumManifestBytes = 512 * 1024;
    public static readonly TimeSpan MaximumManifestAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromSeconds(10);

    public static string ConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GameClub", "Playnite", "config.json");
}
