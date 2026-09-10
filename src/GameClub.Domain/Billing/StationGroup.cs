namespace GameClub.Domain.Billing;

public sealed class StationGroup
{
    public const int MaximumNameLength = 100;

    private StationGroup() { }

    public StationGroup(Guid id, string name)
    {
        if (id == Guid.Empty) throw new ArgumentException("Group id is required.", nameof(id));
        Id = id;
        Name = ValidateName(name);
        NormalizedName = Name.ToUpperInvariant();
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;

    internal static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaximumNameLength)
            throw new ArgumentException($"Name must contain 1 to {MaximumNameLength} characters.", nameof(name));
        return trimmed;
    }
}
