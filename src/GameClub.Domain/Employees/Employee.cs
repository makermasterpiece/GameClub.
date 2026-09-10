namespace GameClub.Domain.Employees;

public enum EmployeeRole
{
    Administrator,
    Manager,
    Operator
}

public sealed class Employee
{
    public const int MinimumUsernameLength = 3;
    public const int MaximumUsernameLength = 64;
    private Employee() { }

    public Employee(Guid id, string username, EmployeeRole role, DateTime createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Employee id is required.", nameof(id));
        username = username?.Trim() ?? string.Empty;
        if (username.Length is < MinimumUsernameLength or > MaximumUsernameLength)
            throw new ArgumentException("Employee username must contain 3 to 64 characters.", nameof(username));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (createdAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Employee time must be UTC.", nameof(createdAtUtc));
        Id = id;
        Username = username;
        NormalizedUsername = NormalizeUsername(username);
        Role = role;
        CreatedAtUtc = createdAtUtc;
        IsActive = true;
        SecurityStamp = Guid.NewGuid();
    }

    public Guid Id { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string NormalizedUsername { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public EmployeeRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public Guid SecurityStamp { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash) || passwordHash.Length > 1024)
            throw new ArgumentException("A bounded employee password hash is required.", nameof(passwordHash));
        PasswordHash = passwordHash;
        RevokeSessions();
    }

    public void ChangeAccess(EmployeeRole role, bool isActive)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (Role == role && IsActive == isActive) return;
        Role = role;
        IsActive = isActive;
        RevokeSessions();
    }

    public void RevokeSessions() => SecurityStamp = Guid.NewGuid();

    public void RecordLogin(DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("Employee time must be UTC.", nameof(now));
        LastLoginAtUtc = now;
    }

    public static string NormalizeUsername(string username) => username.Trim().ToUpperInvariant();
}
