using System.Globalization;
using GameClub.Agent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Services.Client;

public sealed class SqlitePlayerSessionStateStore : IPlayerSessionStateStore
{
    private readonly string _connectionString;
    private readonly object _sync = new();

    public SqlitePlayerSessionStateStore(IOptions<SecurityOptions> options)
    {
        var path = ResolvePath(options.Value.ProcessedCommandStorePath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Agent state database path must have a parent directory.");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        Initialize();
    }

    public Task<PersistedPlayerSession?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT session_id, user_id, username, display_name, created_at_utc, expires_at_utc
                FROM player_session_state
                WHERE id = 1
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult<PersistedPlayerSession?>(null);
            }

            if (!Guid.TryParse(reader.GetString(0), out var sessionId) || sessionId == Guid.Empty ||
                !Guid.TryParse(reader.GetString(1), out var userId) || userId == Guid.Empty)
            {
                throw new InvalidDataException("Persisted player session identity is invalid.");
            }

            var session = new PersistedPlayerSession(
                sessionId,
                userId,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)));
            return Task.FromResult<PersistedPlayerSession?>(session);
        }
    }

    public Task SaveAsync(PersistedPlayerSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(session);
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO player_session_state (
                    id, session_id, user_id, username, display_name, created_at_utc, expires_at_utc)
                VALUES (1, $sessionId, $userId, $username, $displayName, $createdAtUtc, $expiresAtUtc)
                ON CONFLICT(id) DO UPDATE SET
                    session_id = excluded.session_id,
                    user_id = excluded.user_id,
                    username = excluded.username,
                    display_name = excluded.display_name,
                    created_at_utc = excluded.created_at_utc,
                    expires_at_utc = excluded.expires_at_utc
                """;
            command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$userId", session.UserId.ToString("D"));
            command.Parameters.AddWithValue("$username", session.Username);
            command.Parameters.AddWithValue(
                "$displayName",
                session.DisplayName is null ? DBNull.Value : session.DisplayName);
            command.Parameters.AddWithValue("$createdAtUtc", FormatUtc(session.CreatedAtUtc));
            command.Parameters.AddWithValue("$expiresAtUtc", FormatUtc(session.ExpiresAtUtc));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM player_session_state WHERE id = 1";
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS player_session_state (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                session_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                username TEXT NOT NULL,
                display_name TEXT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Validate(PersistedPlayerSession session)
    {
        if (session.SessionId == Guid.Empty || session.UserId == Guid.Empty ||
            string.IsNullOrWhiteSpace(session.Username) || session.Username.Length > 32 ||
            session.DisplayName?.Length > 100 || session.ExpiresAtUtc <= session.CreatedAtUtc)
        {
            throw new ArgumentException("Player session state is invalid.", nameof(session));
        }
    }

    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "GameClub", "Agent", "agent_state.db");
    }
}
