using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using GameClub.Application.Abstractions;
using GameClub.Domain.Commands;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Commands;
using GameClub.Server.Controllers;
using GameClub.Server.Security;
using GameClub.Server.Security.Employees;
using GameClub.Server.Services.Commands;
using GameClub.Server.Services.Players;
using GameClub.Server.Tests.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GameClub.Server.Tests.Commands;

// PostgreSQL/application tests only. The transport is disconnected and no Windows/native power service is constructed.
[Collection(PostgresCollection.Name)]
public sealed class PowerCommandIntegrationTests(PostgresFixture postgres)
{
    private const string Password = "PowerLeaseTestOnly!234";
    private static readonly Guid EmployeeId = Guid.Parse("581e18ef-829e-4907-9bdf-c7063fa4be6f");

    [PostgresFact]
    public Task PendingPowerLease_BlocksRealPlayerLoginUntilGraceExpires() => VerifyLeaseAsync(completed: false);

    [PostgresFact]
    public Task CompletedPowerLease_BlocksRealPlayerLoginUntilGraceExpires() => VerifyLeaseAsync(completed: true);

    private async Task VerifyLeaseAsync(bool completed)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var stationId = await SeedAsync(database, clock);
        await using (var seed = database.CreateContext())
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var command = new AgentCommand(Guid.NewGuid(), stationId, AgentCommandType.RestartStation, null,
                now, now + StationPowerPolicy.CommandLifetime);
            if (completed)
            {
                command.MarkSent(now);
                command.Acknowledge(now);
                command.Complete(now); // State setup only, never a dispatched/native command.
            }
            seed.AgentCommands.Add(command);
            await seed.SaveChangesAsync();
        }

        foreach (var advance in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(58) })
        {
            clock.Advance(advance);
            await using var context = database.CreateContext();
            var result = await Authentication(context, clock).LoginAsync(stationId, "power-player", Password, null, default);
            Assert.Equal(PlayerLoginError.StationUnavailable, result.Error);
            Assert.Empty(await context.PlayerAuthSessions.ToListAsync());
        }

        clock.Advance(TimeSpan.FromSeconds(2)); // 91s > 30s TTL + 60s grace.
        await using var resumed = database.CreateContext();
        var station = await resumed.Stations.SingleAsync();
        station.UpdateFromAgent(station.Name, station.MachineName, station.IpAddress, station.AgentVersion, clock.GetUtcNow().UtcDateTime);
        await resumed.SaveChangesAsync();
        var successful = await Authentication(resumed, clock).LoginAsync(stationId, "power-player", Password, null, default);
        Assert.True(successful.Succeeded);
        Assert.Single(await resumed.PlayerAuthSessions.Where(s => s.Status == PlayerAuthSessionStatus.Active).ToListAsync());
    }

    [PostgresFact]
    public async Task RejectedPowerCreate_AuditsOutsideRollback_WithoutResavingTrackedCommand()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var stationId = await SeedAsync(database, clock);
        foreach (var reason in new[] { "STATION_BUSY", "STATION_OFFLINE", "STATION_POWER_PENDING" })
        {
            await using var context = database.CreateContext();
            var controller = Controller(context, clock, new FailingCreateService(context, clock, new ClubException(reason)));
            var response = await controller.Create(stationId, new CreateAgentCommandRequest("ShutdownStation", null), default);
            var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
            Assert.Equal(reason, Code(conflict.Value));
            await using var verify = database.CreateContext();
            Assert.Empty(await verify.AgentCommands.ToListAsync());
            var audit = await verify.SecurityAuditEvents.OrderByDescending(a => a.TimestampUtc)
                .Where(a => a.EventType == "EmployeeStationPowerRejected" && a.Details!.Contains(reason)).SingleAsync();
            Assert.Contains(EmployeeId.ToString("D"), audit.Details);
            Assert.Contains("ShutdownStation", audit.Details);
        }
    }

    [PostgresFact]
    public async Task NestedPostgresSerializationAndDeadlock_Are409_AndKeepRejectionAudit()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var stationId = await SeedAsync(database, clock);
        foreach (var sqlState in new[] { PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected })
        {
            await using var context = database.CreateContext();
            var failure = WrappedDatabaseError(sqlState);
            var controller = Controller(context, clock, new FailingCreateService(context, clock, failure));
            var response = await controller.Create(stationId, new CreateAgentCommandRequest("RestartStation", null), default);
            Assert.Equal("CONCURRENT_CONFLICT", Code(Assert.IsType<ConflictObjectResult>(response.Result).Value));
        }
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.AgentCommands.ToListAsync());
        var entries = await verify.SecurityAuditEvents.Where(a => a.EventType == "EmployeeStationPowerRejected").ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, audit => Assert.Contains("CONCURRENT_CONFLICT", audit.Details));
        Assert.All(entries, audit => Assert.DoesNotContain("Synthetic database failure", audit.Details));
    }

    [PostgresFact]
    public async Task UnrelatedDatabaseFailure_IsNotMislabeledAsConcurrencyConflict()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var stationId = await SeedAsync(database, clock);
        await using var context = database.CreateContext();
        var failure = WrappedDatabaseError(PostgresErrorCodes.UniqueViolation);
        var controller = Controller(context, clock, new FailingCreateService(context, clock, failure));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Create(stationId, new CreateAgentCommandRequest("RestartStation", null), default));
        Assert.Same(failure, actual);
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.AgentCommands.ToListAsync());
        Assert.Empty(await verify.SecurityAuditEvents.ToListAsync());
    }

    [PostgresFact]
    public async Task RealBusyAndOfflinePowerChecks_Return409WithEmployeeAudit()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var stationId = await SeedAsync(database, clock);
        await using (var seed = database.CreateContext())
        {
            var user = await seed.Users.SingleAsync();
            var now = clock.GetUtcNow().UtcDateTime;
            seed.PlayerAuthSessions.Add(new PlayerAuthSession(Guid.NewGuid(), user.Id, stationId, now, now.AddHours(12)));
            await seed.SaveChangesAsync();
        }
        await using (var context = database.CreateContext())
        {
            var result = await Controller(context, clock, Commands(context, clock)).Create(stationId,
                new CreateAgentCommandRequest("RestartStation", null), default);
            Assert.Equal("STATION_BUSY", Code(Assert.IsType<ConflictObjectResult>(result.Result).Value));
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        await using (var context = database.CreateContext())
        {
            var result = await Controller(context, clock, Commands(context, clock)).Create(stationId,
                new CreateAgentCommandRequest("ShutdownStation", null), default);
            Assert.Equal("STATION_OFFLINE", Code(Assert.IsType<ConflictObjectResult>(result.Result).Value));
        }
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.AgentCommands.ToListAsync());
        Assert.Equal(2, await verify.SecurityAuditEvents.CountAsync(a => a.EventType == "EmployeeStationPowerRejected"));
    }

    [PostgresFact]
    public async Task ConcurrentPowerCreateAndPlayerLogin_HaveOnlyOneWinner()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var stationId = await SeedAsync(database, clock);
        await using var source = database.CreateContext();
        var gate = new LeaseReadGate();
        var connection = source.Database.GetConnectionString();
        await using var powerContext = GatedContext(connection!, gate);
        await using var loginContext = GatedContext(connection!, gate);
        var power = Controller(powerContext, clock, Commands(powerContext, clock)).Create(stationId,
            new CreateAgentCommandRequest("RestartStation", null), default);
        var login = Authentication(loginContext, clock).LoginAsync(stationId, "power-player", Password, null, default);
        await Task.WhenAll(power, login);
        var powerCreated = power.Result.Result is CreatedAtRouteResult;
        Assert.NotEqual(powerCreated, login.Result.Succeeded);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.AgentCommands.CountAsync() +
                        await verify.PlayerAuthSessions.CountAsync(a => a.Status == PlayerAuthSessionStatus.Active));
        if (!powerCreated) Assert.IsType<ConflictObjectResult>(power.Result.Result);
        else Assert.Equal(PlayerLoginError.StationUnavailable, login.Result.Error);
    }

    private static StationCommandsController Controller(GameClubDbContext db, Clock clock, IAgentCommandService commands) =>
        new(db, commands, new SecurityAuditService(db, clock, NullLogger<SecurityAuditService>.Instance), new AllowPower())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(EmployeeAuthenticationDefaults.EmployeeIdClaim, EmployeeId.ToString("D"))],
                        EmployeeAuthenticationDefaults.Scheme))
                }
            }
        };

    private static PlayerAuthenticationService Authentication(GameClubDbContext db, Clock clock) =>
        new(db, new ClubData(db), new AspNetPasswordHasherService(new PasswordHasher<User>()),
            new PlayerLoginRateLimiter(), new SecurityAuditService(db, clock, NullLogger<SecurityAuditService>.Instance),
            clock, NullLogger<PlayerAuthenticationService>.Instance);

    private static AgentCommandService Commands(GameClubDbContext db, Clock clock) =>
        new(db, new DisconnectedTransport(), new NoSigner(), clock, NullLogger<AgentCommandService>.Instance);

    private static async Task<Guid> SeedAsync(PostgresTestDatabase database, Clock clock)
    {
        await using var context = database.CreateContext();
        var now = clock.GetUtcNow().UtcDateTime;
        var station = new Station(Guid.NewGuid(), "Power test PC", "POWER-" + Guid.NewGuid().ToString("N"),
            "127.0.0.1", "0.1.0", now);
        var user = new User(Guid.NewGuid(), "power-player", "Power test player", null, null, now);
        user.SetPasswordHash(new PasswordHasher<User>().HashPassword(user, Password));
        context.Stations.Add(station);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return station.Id;
    }

    private static string? Code(object? value) => JsonSerializer.SerializeToElement(value).GetProperty("code").GetString();
    private static InvalidOperationException WrappedDatabaseError(string sqlState) => new("Synthetic provider wrapper",
        new DbUpdateException("Synthetic update wrapper", new PostgresException("Synthetic database failure", "ERROR", "ERROR", sqlState)));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class AllowPower : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Assert.Equal(EmployeePermissions.Power, policyName);
            return Task.FromResult(AuthorizationResult.Success());
        }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => throw new NotSupportedException();
    }

    private sealed class FailingCreateService(GameClubDbContext db, Clock clock, Exception failure) : IAgentCommandService
    {
        public async Task<AgentCommand?> CreateCommandAsync(Guid stationId, AgentCommandType type, string? payloadJson, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            db.AgentCommands.Add(new AgentCommand(Guid.NewGuid(), stationId, type, null, now, now.AddSeconds(30)));
            await db.SaveChangesAsync(ct);
            throw failure;
        }
        public Task<AgentCommand?> DispatchCommandAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("A rejected request must never dispatch.");
        public Task DispatchPendingCommandsAsync(Guid stationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandOperationResult> AcknowledgeAsync(Guid id, Guid stationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandOperationResult> CompleteAsync(Guid id, Guid stationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandOperationResult> FailAsync(Guid id, Guid stationId, string? error, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class DisconnectedTransport : IStationCommandTransport
    {
        public bool IsConnected(Guid stationId) => false;
        public Task SendAsync(Guid stationId, SignedAgentCommandEnvelope command, CancellationToken ct) =>
            throw new InvalidOperationException("Integration tests must never send a power command to an Agent.");
    }

    private sealed class NoSigner : IServerCommandSigner
    {
        public string PublicKeyBase64 => string.Empty;
        public SignedAgentCommandEnvelope CreateSignedEnvelope(AgentCommand command) => throw new NotSupportedException();
    }

    private static GameClubDbContext GatedContext(string connection, LeaseReadGate gate) =>
        new(new DbContextOptionsBuilder<GameClubDbContext>().UseNpgsql(connection)
            .AddInterceptors(new LeaseReadInterceptor(gate)).EnableSensitiveDataLogging(false).Options);

    private sealed class LeaseReadGate
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class LeaseReadInterceptor(LeaseReadGate gate) : DbCommandInterceptor
    {
        private int _waited;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("agent_commands", StringComparison.Ordinal) && Interlocked.Exchange(ref _waited, 1) == 0)
                await gate.ArriveAsync(cancellationToken);
            return result;
        }
    }
}
