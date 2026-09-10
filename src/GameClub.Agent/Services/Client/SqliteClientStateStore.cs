using System.Globalization;
using GameClub.Agent.Configuration;
using GameClub.Contracts.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Services.Client;

public sealed class SqliteClientStateStore : IClientStateStore
{
    private readonly string _connectionString;
    private readonly object _sync = new();

    public SqliteClientStateStore(IOptions<SecurityOptions> options)
    {
        var path = ResolvePath(options.Value.ProcessedCommandStorePath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Agent state database path must have a parent directory.");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public Task<PersistedClientState?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state, updated_at_utc FROM client_state WHERE id = 1";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return Task.FromResult<PersistedClientState?>(null);
            }

            if (!Enum.TryParse<ClientShellState>(reader.GetString(0), out var state) ||
                !Enum.IsDefined(state))
            {
                throw new InvalidDataException("Persisted client shell state is invalid.");
            }

            var updatedAtUtc = DateTime.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            return Task.FromResult<PersistedClientState?>(new PersistedClientState(state, updatedAtUtc));
        }
    }

    public Task SaveAsync(
        ClientShellState state,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(state) || state == ClientShellState.Offline)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Offline is a connection view, not persisted station state.");
        }

        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO client_state (id, state, updated_at_utc)
                VALUES (1, $state, $updatedAtUtc)
                ON CONFLICT(id) DO UPDATE SET
                    state = excluded.state,
                    updated_at_utc = excluded.updated_at_utc
                """;
            command.Parameters.AddWithValue("$state", state.ToString());
            command.Parameters.AddWithValue(
                "$updatedAtUtc",
                updatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS client_state (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                state TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
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
