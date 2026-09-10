using System.Text.Json;
using System.Text.Json.Serialization;
using GameClub.Contracts.Games;

namespace GameClub.Agent.Services.Games;

public interface ILocalPlayniteLibrary
{
    Task<IReadOnlyList<StationGameInventoryEntry>> ReadInventoryAsync(CancellationToken ct);
    Task RequireInstalledAsync(Guid gameId, Guid playniteId, CancellationToken ct);
    Task RequireAvailableAsync(CancellationToken ct);
}

public sealed class LocalPlayniteLibrary(TimeProvider timeProvider) : ILocalPlayniteLibrary
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<IReadOnlyList<StationGameInventoryEntry>> ReadInventoryAsync(CancellationToken ct)
    {
        var config = await ReadConfigurationAsync(ct);
        if (config is null) return [];
        HashSet<Guid> installed = [];
        try
        {
            ValidatePath(config.FullscreenExecutable, true);
            if (!File.Exists(config.FullscreenExecutable)) throw new IOException("PLAYNITE_UNAVAILABLE");
            installed = (await ReadManifestAsync(config, ct)).Games.Where(x => x.IsInstalled).Select(x => x.Id).ToHashSet();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { /* Report unavailable, never preserve an old installed=true snapshot. */ }
        return config.AllowedGames.Select(x => new StationGameInventoryEntry(x.GameId, x.PlayniteGameId, installed.Contains(x.PlayniteGameId))).ToArray();
    }

    public async Task RequireAvailableAsync(CancellationToken ct)
    {
        var config = await ReadConfigurationAsync(ct) ?? throw new InvalidOperationException("PLAYNITE_DISABLED");
        if (config.AllowedGames.Count == 0) throw new InvalidOperationException("PLAYNITE_DISABLED");
        ValidatePath(config.FullscreenExecutable, true);
        if (!File.Exists(config.FullscreenExecutable)) throw new IOException("PLAYNITE_UNAVAILABLE");
        // Opening the exporter itself must work after Playnite was stopped and its manifest expired.
    }

    public async Task RequireInstalledAsync(Guid gameId, Guid playniteId, CancellationToken ct)
    {
        if (gameId == Guid.Empty || playniteId == Guid.Empty ||
            !(await ReadInventoryAsync(ct)).Any(x => x.GameId == gameId && x.PlayniteGameId == playniteId && x.Installed))
            throw new InvalidOperationException("GAME_NOT_INSTALLED");
    }

    private static async Task<PlayniteLocalConfiguration?> ReadConfigurationAsync(CancellationToken ct)
    {
        ValidatePath(PlaynitePolicy.ConfigurationPath, false);
        if (!File.Exists(PlaynitePolicy.ConfigurationPath)) return null;
        var config = await ReadJsonAsync<PlayniteLocalConfiguration>(PlaynitePolicy.ConfigurationPath, PlaynitePolicy.MaximumConfigurationBytes, ct);
        ValidateConfiguration(config);
        return config;
    }

    public static void ValidateConfiguration(PlayniteLocalConfiguration config)
    {
        ValidatePath(config.FullscreenExecutable, true);
        ValidatePath(config.LibraryManifestPath, false);
        if (config.AllowedGames is null || config.AllowedGames.Count > PlaynitePolicy.MaximumGames ||
            config.AllowedGames.Any(x => x is null || x.GameId == Guid.Empty || x.PlayniteGameId == Guid.Empty) ||
            config.AllowedGames.Select(x => x.GameId).Distinct().Count() != config.AllowedGames.Count ||
            config.AllowedGames.Select(x => x.PlayniteGameId).Distinct().Count() != config.AllowedGames.Count)
            throw new ArgumentException("INVALID_PLAYNITE_CONFIGURATION");
    }

    private async Task<PlayniteLibraryManifest> ReadManifestAsync(PlayniteLocalConfiguration config, CancellationToken ct)
    {
        ValidatePath(config.LibraryManifestPath, false);
        var manifest = await ReadJsonAsync<PlayniteLibraryManifest>(config.LibraryManifestPath, PlaynitePolicy.MaximumManifestBytes, ct);
        ValidateManifest(manifest, timeProvider.GetUtcNow().UtcDateTime);
        return manifest;
    }

    public static void ValidateManifest(PlayniteLibraryManifest manifest, DateTime now)
    {
        if (manifest.SchemaVersion != 1 || manifest.ExportedAtUtc.Kind != DateTimeKind.Utc ||
            manifest.ExportedAtUtc > now || now - manifest.ExportedAtUtc > PlaynitePolicy.MaximumManifestAge ||
            manifest.Games is null || manifest.Games.Count > PlaynitePolicy.MaximumGames ||
            manifest.Games.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 256) ||
            manifest.Games.Select(x => x.Id).Distinct().Count() != manifest.Games.Count)
            throw new ArgumentException("INVALID_PLAYNITE_MANIFEST");
    }

    public static void ValidatePath(string path, bool executable)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path) ||
            path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).Contains(':') || path.Contains('/') || path.Contains('"') ||
            path.Split('\\').Any(x => x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.')) ||
            (executable && !string.Equals(Path.GetFileName(path), "Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("INVALID_PLAYNITE_PATH");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("INVALID_PLAYNITE_PATH");
    }

    private static async Task<T> ReadJsonAsync<T>(string path, int limit, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        var bytes = new byte[limit + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), ct)) != 0) count += read;
        if (count > limit) throw new JsonException("PLAYNITE_FILE_TOO_LARGE");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
        RejectDuplicateKeys(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new JsonException("PLAYNITE_FILE_EMPTY");
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("DUPLICATE_PLAYNITE_PROPERTY");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
