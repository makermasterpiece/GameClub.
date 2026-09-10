using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameClub.Contracts.Games;

namespace GameClub.Client.Services.Playnite;

public interface IPlayniteConfigurationReader
{
    PlayniteLocalConfiguration Read();
}

public sealed class PlayniteConfigurationReader : IPlayniteConfigurationReader
{
    public PlayniteLocalConfiguration Read()
    {
        ValidatePath(PlaynitePolicy.ConfigurationPath, false);
        using var stream = File.OpenRead(PlaynitePolicy.ConfigurationPath);
        var bytes = new byte[PlaynitePolicy.MaximumConfigurationBytes + 1];
        var count = 0;
        int read;
        while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) != 0) count += read;
        if (count > PlaynitePolicy.MaximumConfigurationBytes) throw new IOException("Invalid Playnite configuration size.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
        RejectDuplicateKeys(document.RootElement);
        var configuration = document.RootElement.Deserialize<PlayniteLocalConfiguration>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (configuration is null || !Path.IsPathFullyQualified(configuration.FullscreenExecutable)
            || !string.Equals(Path.GetFileName(configuration.FullscreenExecutable), "Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(configuration.LibraryManifestPath)
            || configuration.AllowedGames is null || configuration.AllowedGames.Count is 0 or > PlaynitePolicy.MaximumGames
            || configuration.AllowedGames.Any(x => x is null || x.GameId == Guid.Empty || x.PlayniteGameId == Guid.Empty)
            || configuration.AllowedGames.Select(x => x.GameId).Distinct().Count() != configuration.AllowedGames.Count
            || configuration.AllowedGames.Select(x => x.PlayniteGameId).Distinct().Count() != configuration.AllowedGames.Count)
            throw new IOException("Invalid Playnite configuration.");
        ValidatePath(configuration.FullscreenExecutable, true);
        ValidatePath(configuration.LibraryManifestPath, false);
        return configuration;
    }

    public static void ValidatePath(string path, bool executable)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path)
            || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.AsSpan(2).Contains(':') || path.Contains('/') || path.Contains('"')
            || path.Split('\\').Any(x => x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.'))
            || (executable && !string.Equals(Path.GetFileName(path), "Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Invalid Playnite path.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Invalid Playnite path.");
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate Playnite configuration property.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
