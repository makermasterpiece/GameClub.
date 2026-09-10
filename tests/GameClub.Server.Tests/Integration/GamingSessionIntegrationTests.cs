using System.Collections.Concurrent;
using GameClub.Application.Abstractions;
using GameClub.Application.Gaming;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GameClub.Server.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class GamingSessionIntegrationTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task MigratedDatabase_CurrentSurvivesRestartAndStaleContextCannotReopenEndedSession()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        await using var firstContext = database.CreateContext();
        Assert.Empty(await firstContext.Database.GetPendingMigrationsAsync());
        var firstService = CreateService(firstContext, clock);
        var started = await firstService.StartAsync(seed.UserId, seed.StationId, 30, null, default);
        var sessionId = started.Id;
        Assert.Equal("Active", started.Status);
        Assert.Equal(1800, started.RemainingSeconds);

        clock.Advance(TimeSpan.FromSeconds(42));
        await using var restartedContext = database.CreateContext();
        var current = await CreateService(restartedContext, clock).CurrentAsync(seed.StationId, default);
        Assert.NotNull(current.Session);
        Assert.Equal(sessionId, current.Session.Id);
        Assert.Equal(seed.UserId, current.Session.UserId);
        Assert.Equal(1758, current.Session.RemainingSeconds);
        Assert.Equal(42, current.Session.ElapsedSeconds);
        Assert.Single(await restartedContext.Set<SessionEvent>().ToListAsync());

        await CreateService(restartedContext, clock).EndAsync(sessionId, null, default);
        var now = clock.GetUtcNow().UtcDateTime;
        restartedContext.PlayerAuthSessions.Add(new PlayerAuthSession(Guid.NewGuid(), seed.UserId,
            seed.StationId, now, now.AddHours(12)));
        await restartedContext.SaveChangesAsync();
        // Reauthentication is valid, but an old tracked Active entity must not revive a completed game.
        var error = await Assert.ThrowsAsync<ClubException>(() => firstService.PauseAsync(sessionId, null, default));
        Assert.Equal("INVALID_SESSION_TRANSITION", error.Code);
        await using var verify = database.CreateContext();
        Assert.Equal(GamingSessionStatus.Completed, (await verify.Set<GamingSession>().SingleAsync()).Status);
        Assert.Equal(1, await verify.Set<SessionEvent>().CountAsync(e => e.Type == SessionEventType.SessionEnded));
        Assert.Equal(0, await verify.Set<SessionEvent>().CountAsync(e => e.Type == SessionEventType.SessionPaused));
    }

    [PostgresFact]
    public async Task PauseResumeAndAutomaticEnd_PersistEventsAndEndAuthentication()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        var notifications = new RecordingEvents();
        await using var context = database.CreateContext();
        var service = CreateService(context, clock, notifications);
        var started = await service.StartAsync(seed.UserId, seed.StationId, 2, null, default);

        clock.Advance(TimeSpan.FromSeconds(20));
        var paused = await service.PauseAsync(started.Id, null, default);
        Assert.Equal("Paused", paused.Status);
        Assert.Equal(100, paused.RemainingSeconds);
        Assert.Null(paused.ExpectedEndAtUtc);
        clock.Advance(TimeSpan.FromSeconds(30));
        var whilePaused = await service.CurrentAsync(seed.StationId, default);
        Assert.Equal(100, whilePaused.Session?.RemainingSeconds);
        Assert.Equal(0, await service.CompleteDueAsync(default));

        var resumed = await service.ResumeAsync(started.Id, null, default);
        var deadline = clock.GetUtcNow().UtcDateTime.AddSeconds(100);
        Assert.Equal(deadline, resumed.ExpectedEndAtUtc);
        clock.Advance(TimeSpan.FromSeconds(105));
        Assert.Equal(1, await service.CompleteDueAsync(default));
        Assert.Equal(0, await service.CompleteDueAsync(default));

        await using var persisted = database.CreateContext();
        var session = await persisted.Set<GamingSession>().SingleAsync();
        Assert.Equal(GamingSessionStatus.Completed, session.Status);
        Assert.Equal(deadline, session.EndedAtUtc);
        Assert.Equal(120, session.AccumulatedSeconds);
        Assert.Null((await CreateService(persisted, clock).CurrentAsync(seed.StationId, default)).Session);
        Assert.Equal(PlayerAuthSessionStatus.Ended, (await persisted.PlayerAuthSessions.SingleAsync()).Status);
        var eventTypes = await persisted.Set<SessionEvent>().OrderBy(e => e.CreatedAtUtc).Select(e => e.Type).ToListAsync();
        Assert.Equal(new[] { SessionEventType.SessionStarted, SessionEventType.SessionPaused,
            SessionEventType.SessionResumed, SessionEventType.SessionEnded }, eventTypes);
        Assert.Equal(4, notifications.StationIds.Count);
        Assert.All(notifications.StationIds, id => Assert.Equal(seed.StationId, id));
        await CreateService(persisted, clock).EndAsync(started.Id, null, default);
        Assert.Equal(4, await persisted.Set<SessionEvent>().CountAsync());
        Assert.Equal(deadline, (await persisted.Set<GamingSession>().SingleAsync()).EndedAtUtc);
    }

    [PostgresFact]
    public async Task UniqueOpenSessionPerUser_AlsoProtectsPausedSessionsAtDatabaseLevel()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        await using var context = database.CreateContext();
        var secondStation = NewStation(clock);
        context.Stations.Add(secondStation);
        var first = NewActiveSession(seed.UserId, seed.StationId, clock);
        first.Pause(clock.GetUtcNow().UtcDateTime);
        context.Set<GamingSession>().Add(first);
        await context.SaveChangesAsync();

        context.Set<GamingSession>().Add(NewActiveSession(seed.UserId, secondStation.Id, clock));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        await using var verify = database.CreateContext();
        Assert.Single(await verify.Set<GamingSession>().ToListAsync());
    }

    [PostgresFact]
    public async Task UniqueOpenSessionPerStation_IsEnforcedAtDatabaseLevel()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        await using var context = database.CreateContext();
        var secondUser = NewUser(clock);
        context.Users.Add(secondUser);
        context.Set<GamingSession>().Add(NewActiveSession(seed.UserId, seed.StationId, clock));
        await context.SaveChangesAsync();

        context.Set<GamingSession>().Add(NewActiveSession(secondUser.Id, seed.StationId, clock));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        await using var verify = database.CreateContext();
        Assert.Single(await verify.Set<GamingSession>().ToListAsync());
    }

    [PostgresFact]
    public async Task ConcurrentStarts_CommitExactlyOneSessionAndOneEvent()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        var gate = new TwoOperationGate();
        var notifications = new RecordingEvents();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = new GamingSessionService(new GatedData(new ClubData(firstContext), gate), clock, notifications);
        var second = new GamingSessionService(new GatedData(new ClubData(secondContext), gate), clock, notifications);

        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => first.StartAsync(seed.UserId, seed.StationId, 30, null, default)),
            Record.ExceptionAsync(() => second.StartAsync(seed.UserId, seed.StationId, 30, null, default)));

        Assert.Single(outcomes, error => error is null);
        var rejection = Assert.IsType<ClubException>(Assert.Single(outcomes, error => error is not null));
        Assert.Contains(rejection.Code, new[] { "SESSION_ALREADY_ACTIVE", "CONCURRENT_CONFLICT" });
        await using var verify = database.CreateContext();
        Assert.Single(await verify.Set<GamingSession>().ToListAsync());
        var sessionEvent = Assert.Single(await verify.Set<SessionEvent>().ToListAsync());
        Assert.Equal(SessionEventType.SessionStarted, sessionEvent.Type);
        Assert.Single(notifications.StationIds);
    }

    [PostgresFact]
    public async Task EventInsertFailure_RollsBackGamingSessionAndDoesNotPublish()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        var notifications = new RecordingEvents();
        await using var context = database.CreateContext();
        // Deliberately fail a real SQL write, after EF has begun the atomic session/event transaction.
        // This trigger exists only inside this test's freshly generated, disposable database.
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION gameclub_test_reject_session_event() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Integration test event write failure'; END $$;
            CREATE TRIGGER gameclub_test_reject_session_event
            BEFORE INSERT ON session_events FOR EACH ROW
            EXECUTE FUNCTION gameclub_test_reject_session_event();
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            CreateService(context, clock, notifications).StartAsync(seed.UserId, seed.StationId, 30, null, default));

        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Set<GamingSession>().ToListAsync());
        Assert.Empty(await verify.Set<SessionEvent>().ToListAsync());
        Assert.Equal(PlayerAuthSessionStatus.Active, (await verify.PlayerAuthSessions.SingleAsync()).Status);
        Assert.Empty(notifications.StationIds);
    }

    [PostgresFact]
    public async Task ConcurrentLogoutAndStart_NeverLeaveAnActiveGameWithoutAuthentication()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, clock);
        var gate = new TwoOperationGate();
        var notifications = new RecordingEvents();
        await using var startContext = database.CreateContext();
        await using var logoutContext = database.CreateContext();
        var startService = new GamingSessionService(new GatedData(new ClubData(startContext), gate), clock, notifications);
        var logoutService = new GamingSessionService(new GatedData(new ClubData(logoutContext), gate), clock, notifications);

        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => startService.StartAsync(seed.UserId, seed.StationId, null, null, default)),
            Record.ExceptionAsync(() => logoutService.LogoutStationAsync(seed.StationId, "127.0.0.1", default)));

        Assert.Null(outcomes[1]);
        if (outcomes[0] is not null)
            Assert.Equal("PLAYER_NOT_AUTHENTICATED", Assert.IsType<ClubException>(outcomes[0]).Code);
        await using var verify = database.CreateContext();
        Assert.False(await verify.PlayerAuthSessions.AnyAsync(a => a.Status == PlayerAuthSessionStatus.Active));
        Assert.False(await verify.Set<GamingSession>().AnyAsync(s =>
            s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused));
        Assert.Equal(await verify.Set<SessionEvent>().CountAsync(e => e.Type == SessionEventType.SessionStarted),
            await verify.Set<SessionEvent>().CountAsync(e => e.Type == SessionEventType.SessionEnded));
        Assert.Equal(1, await verify.SecurityAuditEvents.CountAsync(e => e.EventType == "PlayerLogout"));
    }

    [PostgresFact]
    public async Task ExpiredDisabledOrBannedAuthentication_ClosesActiveAndPausedGames()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var expired = await SeedAsync(database, clock);
        var disabled = await SeedAsync(database, clock);
        var banned = await SeedAsync(database, clock);
        await using (var context = database.CreateContext())
        {
            var service = CreateService(context, clock);
            await service.StartAsync(expired.UserId, expired.StationId, null, null, default);
            var paused = await service.StartAsync(disabled.UserId, disabled.StationId, null, null, default);
            await service.PauseAsync(paused.Id, null, default);
            await service.StartAsync(banned.UserId, banned.StationId, null, null, default);
            (await context.Users.SingleAsync(u => u.Id == disabled.UserId)).ChangeStatus(UserStatus.Disabled);
            (await context.Users.SingleAsync(u => u.Id == banned.UserId)).ChangeStatus(UserStatus.Banned);
            await context.SaveChangesAsync();
        }

        await using (var invalidated = database.CreateContext())
        {
            var service = CreateService(invalidated, clock);
            Assert.Null((await service.CurrentAsync(disabled.StationId, default)).Session);
            Assert.Equal(1, await service.CompleteDueAsync(default));
            Assert.Null((await service.CurrentAsync(banned.StationId, default)).Session);
        }

        clock.Advance(TimeSpan.FromHours(13));
        await using var restarted = database.CreateContext();
        Assert.Null((await CreateService(restarted, clock).CurrentAsync(expired.StationId, default)).Session);
        Assert.All(await restarted.Set<GamingSession>().ToListAsync(), s => Assert.Equal(GamingSessionStatus.Completed, s.Status));
        Assert.False(await restarted.PlayerAuthSessions.AnyAsync(a => a.Status == PlayerAuthSessionStatus.Active));
        Assert.Equal(PlayerAuthSessionStatus.Expired,
            (await restarted.PlayerAuthSessions.SingleAsync(a => a.UserId == expired.UserId)).Status);
        Assert.Equal(3, await restarted.Set<SessionEvent>().CountAsync(e => e.Type == SessionEventType.SessionEnded));
    }

    private static GamingSessionService CreateService(GameClubDbContext context, TimeProvider clock,
        RecordingEvents? events = null) => new(new ClubData(context), clock, events ?? new RecordingEvents());

    private static async Task<Seed> SeedAsync(PostgresTestDatabase database, MutableClock clock)
    {
        await using var context = database.CreateContext();
        var station = NewStation(clock);
        var user = NewUser(clock);
        var now = clock.GetUtcNow().UtcDateTime;
        context.Stations.Add(station);
        context.Users.Add(user);
        context.PlayerAuthSessions.Add(new PlayerAuthSession(Guid.NewGuid(), user.Id, station.Id, now, now.AddHours(12)));
        await context.SaveChangesAsync();
        return new Seed(user.Id, station.Id);
    }

    private static Station NewStation(MutableClock clock) => new(Guid.NewGuid(), "Test PC",
        "TEST-" + Guid.NewGuid().ToString("N"), "127.0.0.1", "0.1.0", clock.GetUtcNow().UtcDateTime);

    private static User NewUser(MutableClock clock)
    {
        var user = new User(Guid.NewGuid(), "test_" + Guid.NewGuid().ToString("N")[..12],
            "Integration player", null, null, clock.GetUtcNow().UtcDateTime);
        user.SetPasswordHash(new PasswordHasher<User>().HashPassword(user, "IntegrationOnly!234"));
        return user;
    }

    private static GamingSession NewActiveSession(Guid userId, Guid stationId, MutableClock clock)
    {
        var session = new GamingSession(Guid.NewGuid(), userId, stationId, 30, clock.GetUtcNow().UtcDateTime);
        session.Start(clock.GetUtcNow().UtcDateTime);
        return session;
    }

    private sealed record Seed(Guid UserId, Guid StationId);

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 5, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingEvents : IClubEvents
    {
        public ConcurrentQueue<Guid> StationIds { get; } = new();
        public Task StationChangedAsync(Guid stationId, CancellationToken cancellationToken)
        {
            StationIds.Enqueue(stationId);
            return Task.CompletedTask;
        }
    }

    private sealed class TwoOperationGate
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private sealed class GatedData(IClubData inner, TwoOperationGate gate) : IClubData
    {
        private int _attempt;
        public IQueryable<T> Query<T>() where T : class => inner.Query<T>();
        public void Add<T>(T entity) where T : class => inner.Add(entity);
        public Task SaveAsync(CancellationToken cancellationToken) => inner.SaveAsync(cancellationToken);
        public Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            inner.AtomicAsync(async token =>
            {
                var result = await operation(token);
                // Both serializable transactions observe no current session before either may commit.
                // Retried operations deliberately bypass this one-time synchronization point.
                if (Interlocked.Increment(ref _attempt) == 1) await gate.ArriveAsync(token);
                return result;
            }, cancellationToken);
    }
}
