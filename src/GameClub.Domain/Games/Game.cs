namespace GameClub.Domain.Games;

/// <summary>Curated catalogue metadata. Executable is never a remote execution instruction.</summary>
public sealed class Game
{
    private Game() { }

    public Game(Guid id, string name, string? playniteGameId, string? executable, string? coverUrl, bool isActive = true)
    {
        if (id == Guid.Empty) throw new ArgumentException("Game id is required.", nameof(id));
        Id = id;
        Update(name, playniteGameId, executable, coverUrl, isActive);
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? PlayniteGameId { get; private set; }
    public string? Executable { get; private set; }
    public string? CoverUrl { get; private set; }
    public bool IsActive { get; private set; }

    public void Update(string name, string? playniteGameId, string? executable, string? coverUrl, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException("Game name must contain 1 to 200 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(playniteGameId)) playniteGameId = null;
        else if (!Guid.TryParse(playniteGameId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Playnite id must be a non-empty GUID.", nameof(playniteGameId));
        else playniteGameId = parsed.ToString("D");
        executable = Normalize(executable, 1024);
        coverUrl = Normalize(coverUrl, 2048);
        if (coverUrl is not null && (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("Cover URL must be an absolute HTTP(S) URL without credentials.", nameof(coverUrl));
        Name = name.Trim();
        PlayniteGameId = playniteGameId;
        Executable = executable;
        CoverUrl = coverUrl;
        IsActive = isActive;
    }

    private static string? Normalize(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Invalid game metadata.");
        return value;
    }
}
