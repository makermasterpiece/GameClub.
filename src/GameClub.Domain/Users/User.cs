namespace GameClub.Domain.Users;

public sealed class User
{
    public const int MinimumUsernameLength = 3;
    public const int MaximumUsernameLength = 32;
    public const int MaximumDisplayNameLength = 100;
    public const int MaximumEmailLength = 320;
    public const int MaximumPhoneLength = 50;

    private User()
    {
    }

    public User(
        Guid id,
        string username,
        string? displayName,
        string? email,
        string? phone,
        DateTime createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(id));
        }

        var trimmedUsername = username?.Trim() ?? string.Empty;
        if (trimmedUsername.Length is < MinimumUsernameLength or > MaximumUsernameLength)
        {
            throw new ArgumentException(
                $"Username length must be between {MinimumUsernameLength} and {MaximumUsernameLength} characters.",
                nameof(username));
        }

        var trimmedDisplayName = NormalizeOptional(displayName);
        if (trimmedDisplayName?.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException(
                $"Display name must not exceed {MaximumDisplayNameLength} characters.",
                nameof(displayName));
        }

        var trimmedEmail = NormalizeOptional(email);
        var trimmedPhone = NormalizeOptional(phone);
        if (trimmedEmail?.Length > MaximumEmailLength)
        {
            throw new ArgumentException(
                $"Email must not exceed {MaximumEmailLength} characters.",
                nameof(email));
        }

        if (trimmedPhone?.Length > MaximumPhoneLength)
        {
            throw new ArgumentException(
                $"Phone must not exceed {MaximumPhoneLength} characters.",
                nameof(phone));
        }

        Id = id;
        Username = trimmedUsername;
        NormalizedUsername = NormalizeUsername(trimmedUsername);
        DisplayName = trimmedDisplayName;
        Email = trimmedEmail;
        Phone = trimmedPhone;
        Status = UserStatus.Active;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
    }

    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string NormalizedUsername { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? DisplayName { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public UserStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }
    public ICollection<PlayerAuthSession> PlayerAuthSessions { get; private set; } = [];

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
    }

    public void ChangeStatus(UserStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
    }

    public void RecordLogin(DateTime utcNow) =>
        LastLoginAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public static string NormalizeUsername(string username) =>
        username.Trim().ToUpperInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
