using System.Collections.Concurrent;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace GameClub.Server.Tests.Integration;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFixture.ConnectionVariable)))
        {
            Skip = $"Set {PostgresFixture.ConnectionVariable} to an isolated PostgreSQL test server with CREATE DATABASE permission.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Isolated PostgreSQL integration tests";
}

/// <summary>
/// Construction and InitializeAsync never connect, so absent opt-in remains a genuine skipped test.
/// Each lease creates and migrates a new database, never reusing the configured database's schema.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "GAMECLUB_TEST_POSTGRES";
    private readonly ConcurrentDictionary<string, PostgresTestDatabase> _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<PostgresTestDatabase> CreateDatabaseAsync(string? targetMigration = null)
    {
        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            throw new InvalidOperationException($"Integration tests require explicit {ConnectionVariable} opt-in.");
        }

        var admin = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            Pooling = false,
            IncludeErrorDetail = false,
            ApplicationName = "GameClub.IntegrationTests",
            Timeout = 10,
            CommandTimeout = 30
        };
        var databaseName = "gameclub_test_" + Guid.NewGuid().ToString("N");
        var testConnection = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Database = databaseName
        };

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var database = new PostgresTestDatabase(databaseName, admin.ConnectionString,
            testConnection.ConnectionString, name => _databases.TryRemove(name, out _));
        if (!_databases.TryAdd(databaseName, database))
        {
            await database.DisposeAsync();
            throw new InvalidOperationException("Generated test database identifier collision.");
        }

        try
        {
            await using var context = database.CreateContext();
            await context.GetService<IMigrator>().MigrateAsync(targetMigration);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var database in _databases.Values)
        {
            await database.DisposeAsync();
        }
    }
}

public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _name;
    private readonly string _adminConnection;
    private readonly string _testConnection;
    private readonly Action<string> _onDisposed;
    private bool _disposed;

    internal PostgresTestDatabase(string name, string adminConnection, string testConnection,
        Action<string> onDisposed)
    {
        _name = name;
        _adminConnection = adminConnection;
        _testConnection = testConnection;
        _onDisposed = onDisposed;
    }

    public GameClubDbContext CreateContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new GameClubDbContext(new DbContextOptionsBuilder<GameClubDbContext>()
            .UseNpgsql(_testConnection)
            .EnableSensitiveDataLogging(false)
            .Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        const string requiredPrefix = "gameclub_test_";
        if (!_name.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(_name[requiredPrefix.Length..], "N", out var identifier) ||
            _name != requiredPrefix + identifier.ToString("N") ||
            new NpgsqlConnectionStringBuilder(_testConnection).Database != _name)
        {
            throw new InvalidOperationException("Refusing to drop a database outside this generated test lease.");
        }

        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        // The identifier is exclusively generated above and revalidated immediately before destruction.
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        _disposed = true;
        _onDisposed(_name);
    }
}
