using System.Globalization;
using GameClub.Agent.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Services.Commands;

public sealed class SqliteProcessedCommandStore : IProcessedCommandStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    public SqliteProcessedCommandStore(
        IOptions<SecurityOptions> options,
        TimeProvider timeProvider)
    {
        var path = ResolvePath(options.Value.ProcessedCommandStorePath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Processed command store path must have a parent directory.");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider;
        Initialize();
    }

    public bool TryBegin(Guid commandId, string nonce)
    {
        lock (_sync)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = "DELETE FROM processed_commands WHERE processed_at_utc < $cutoff";
                prune.Parameters.AddWithValue("$cutoff", Format(Now() - Retention));
                prune.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO processed_commands
                    (command_id, nonce, processed_at_utc, status, error)
                VALUES ($commandId, $nonce, $processedAtUtc, $status, NULL)
                """;
            insert.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            insert.Parameters.AddWithValue("$nonce", nonce);
            insert.Parameters.AddWithValue("$processedAtUtc", Format(Now()));
            insert.Parameters.AddWithValue("$status", ProcessedCommandStatus.Received.ToString());
            var inserted = insert.ExecuteNonQuery() == 1;
            transaction.Commit();
            return inserted;
        }
    }

    public bool TryGet(Guid commandId, out ProcessedCommandState state)
    {
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, error, processed_at_utc
                FROM processed_commands
                WHERE command_id = $commandId
                """;
            command.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                state = null!;
                return false;
            }

            state = new ProcessedCommandState(
                Enum.Parse<ProcessedCommandStatus>(reader.GetString(0), ignoreCase: false),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTime.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind));
            return true;
        }
    }

    public void Set(Guid commandId, ProcessedCommandStatus status, string? error = null)
    {
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE processed_commands
                SET status = $status, error = $error, processed_at_utc = $processedAtUtc
                WHERE command_id = $commandId
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$processedAtUtc", Format(Now()));
            command.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            command.ExecuteNonQuery();
        }
    }

    public void Remove(Guid commandId)
    {
        lock (_sync)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM processed_commands WHERE command_id = $commandId";
            command.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            command.ExecuteNonQuery();
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS processed_commands (
                command_id TEXT NOT NULL PRIMARY KEY,
                nonce TEXT NOT NULL UNIQUE,
                processed_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT NULL
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

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
    private static string Format(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

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
