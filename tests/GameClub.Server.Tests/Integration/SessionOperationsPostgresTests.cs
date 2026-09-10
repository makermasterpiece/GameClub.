using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Application.Gaming;
using GameClub.Domain.Billing;
using GameClub.Domain.Commands;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SessionOperationsPostgresTests(PostgresFixture postgres)
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task PrepaidExtension_QuotesWithoutWritesUsesSnapshotAndReplaysWithoutSecondCharge()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, 6m, 100m);
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        await using (var update = database.CreateContext())
        {
            (await update.Set<Tariff>().SingleAsync()).UpdatePrice(60m);
            await update.SaveChangesAsync();
        }
        var quote = await service.QuoteExtensionAsync(game.Id, 30, default);
        Assert.Equal(3m, quote.Charge);
        Assert.Equal(0m, quote.AdditionalReservation);
        Assert.Equal(Now.AddMinutes(90), quote.ExpectedEndAtUtc);
        Assert.False(await context.Set<SessionOperation>().AnyAsync());
        var operation = Guid.NewGuid();
        await service.ExtendAsync(game.Id, operation, 30, null, default);
        await service.ExtendAsync(game.Id, operation, 30, null, default);
        await service.EndAsync(game.Id, null, default);
        var replay = await service.ExtendAsync(game.Id, operation, 30, null, default);
        Assert.Equal("Completed", replay.Status);
        await using var verify = database.CreateContext();
        var stored = await verify.Set<GamingSession>().SingleAsync();
        Assert.Equal(30, stored.AddedMinutes);
        Assert.Equal(9m, stored.FinalPrice);
        Assert.Equal(91m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Single(await verify.Set<SessionOperation>().ToListAsync());
        Assert.Single(await verify.Set<SessionEvent>().Where(e => e.Type == SessionEventType.SessionExtended).ToListAsync());
    }

    [PostgresFact]
    public async Task PackageExtension_UsesOriginalPackageRatioAfterDeactivation()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, 6m, 100m);
        Guid packageId;
        await using (var setup = database.CreateContext())
        {
            var package = new TariffPackage(Guid.NewGuid(), "Fixed", seed.GroupId, 60, 5m);
            packageId = package.Id;
            setup.Add(package);
            await setup.SaveChangesAsync();
        }
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(new SessionPurchase(Guid.NewGuid(), seed.UserId, seed.SourceId,
            BillingMode.Prepaid, null, packageId, null, null), null, default);
        await using (var update = database.CreateContext())
        {
            (await update.Set<TariffPackage>().SingleAsync()).SetActive(false);
            await update.SaveChangesAsync();
        }
        Assert.Equal(2.5m, (await service.QuoteExtensionAsync(game.Id, 30, default)).Charge);
        await service.ExtendAsync(game.Id, Guid.NewGuid(), 30, null, default);
        await service.EndAsync(game.Id, null, default);
        await using var verify = database.CreateContext();
        Assert.Equal(7.5m, (await verify.Set<GamingSession>().SingleAsync()).FinalPrice);
    }

    [PostgresFact]
    public async Task PostpaidExtension_IncreasesHoldPreservesPauseAndSettlesActualTime()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var clock = new MutableClock();
        var seed = await SeedAsync(database, 6m, 10m);
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed, BillingMode.Postpaid, 1m), null, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.PauseAsync(game.Id, null, default);
        clock.Advance(TimeSpan.FromMinutes(10));
        var quote = await service.QuoteExtensionAsync(game.Id, 5, default);
        Assert.Equal(0.5m, quote.AdditionalReservation);
        Assert.Equal(0m, quote.Charge);
        Assert.Null(quote.ExpectedEndAtUtc);
        var operation = Guid.NewGuid();
        await service.ExtendAsync(game.Id, operation, 5, null, default);
        await service.ExtendAsync(game.Id, operation, 5, null, default);
        await service.ResumeAsync(game.Id, null, default);
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.EndAsync(game.Id, null, default);
        await using var verify = database.CreateContext();
        var stored = await verify.Set<GamingSession>().SingleAsync();
        Assert.Equal(900, stored.FundingLimitSeconds);
        Assert.Equal(0.55m, stored.FinalPrice);
        Assert.Equal(9.45m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        var reservation = await verify.Set<WalletReservation>().SingleAsync();
        Assert.Equal(1.5m, reservation.Amount);
        Assert.NotNull(reservation.ReleasedAtUtc);
    }

    [PostgresFact]
    public async Task UnaffordableExtension_LeavesNoJournalChargeOrBudgetChange()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 6m);
        await using var context = database.CreateContext();
        var service = Service(context, new MutableClock());
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        var error = await Assert.ThrowsAsync<ClubException>(() => service.ExtendAsync(game.Id, Guid.NewGuid(), 1, null, default));
        Assert.Equal("INSUFFICIENT_FUNDS", error.Code);
        await using var verify = database.CreateContext();
        Assert.Equal(0, (await verify.Set<GamingSession>().SingleAsync()).AddedMinutes);
        Assert.False(await verify.Set<SessionOperation>().AnyAsync());
        Assert.False(await verify.Set<SessionEvent>().AnyAsync(e => e.Type == SessionEventType.SessionExtended));
        Assert.Equal(0m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task Transfer_PreservesAuthorizationTtlPauseBudgetAndNotifiesBothStations()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        var notifications = new Notifications();
        await using var context = database.CreateContext();
        var service = Service(context, clock, notifications);
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.PauseAsync(game.Id, null, default);
        notifications.Stations.Clear();
        var operation = Guid.NewGuid();
        var moved = await service.TransferAsync(game.Id, operation, seed.TargetId, null, default);
        await service.TransferAsync(game.Id, operation, seed.TargetId, null, default);
        Assert.Equal("Paused", moved.Status);
        Assert.Equal(3590, moved.RemainingSeconds);
        Assert.Equal(new[] { seed.SourceId, seed.TargetId }, notifications.Stations);
        await using var verify = database.CreateContext();
        var auth = await verify.PlayerAuthSessions.SingleAsync();
        Assert.Equal(seed.AuthId, auth.Id);
        Assert.Equal(seed.TargetId, auth.StationId);
        Assert.Equal(Now.AddHours(12), auth.ExpiresAtUtc);
        var segments = await verify.Set<StationSessionSegment>().OrderBy(s => s.StartedAtUtc).ToListAsync();
        Assert.Equal(2, segments.Count);
        Assert.Equal(seed.SourceId, segments[0].StationId);
        Assert.Equal(Now.AddSeconds(10), segments[0].EndedAtUtc);
        Assert.Equal(seed.TargetId, segments[1].StationId);
        Assert.Null(segments[1].EndedAtUtc);
        Assert.Equal(94m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task DelayedSourceLogout_CannotEndTransferredOrNewOccupantAuthorization()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        await using var context = database.CreateContext();
        var service = Service(context, new MutableClock());
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        await service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default);
        Guid replacementId;
        await using (var setup = database.CreateContext())
        {
            var nextUser = NewUser();
            setup.Add(nextUser);
            var replacement = new PlayerAuthSession(Guid.NewGuid(), nextUser.Id, seed.SourceId, Now, Now.AddHours(12));
            replacementId = replacement.Id;
            setup.Add(replacement);
            await setup.SaveChangesAsync();
        }
        await service.LogoutStationAsync(seed.SourceId, seed.AuthId, null, default);
        await using var verify = database.CreateContext();
        Assert.Equal(GamingSessionStatus.Active, (await verify.Set<GamingSession>().SingleAsync()).Status);
        Assert.Equal(PlayerAuthSessionStatus.Active, (await verify.PlayerAuthSessions.SingleAsync(a => a.Id == seed.AuthId)).Status);
        Assert.Equal(PlayerAuthSessionStatus.Active, (await verify.PlayerAuthSessions.SingleAsync(a => a.Id == replacementId)).Status);
    }

    [PostgresFact]
    public async Task ConcurrentTransfers_ToOneDestination_CommitOnlyOneCompleteMove()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var context1 = database.CreateContext();
        var service1 = Service(context1, clock);
        var game1 = await service1.StartPaidAsync(Purchase(seed), null, default);
        var seed2 = await SeedAsync(database, 6m, 100m);
        await using var context2 = database.CreateContext();
        var service2 = Service(context2, clock);
        var game2 = await service2.StartPaidAsync(Purchase(seed2), null, default);
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => service1.TransferAsync(game1.Id, Guid.NewGuid(), seed.TargetId, null, default)),
            Record.ExceptionAsync(() => service2.TransferAsync(game2.Id, Guid.NewGuid(), seed.TargetId, null, default)));
        Assert.Single(outcomes, x => x is null);
        Assert.Equal("STATION_OCCUPIED", Assert.IsType<ClubException>(Assert.Single(outcomes, x => x is not null)).Code);
        await using var verify = database.CreateContext();
        Assert.Single(await verify.Set<GamingSession>().Where(s => s.StationId == seed.TargetId).ToListAsync());
        Assert.Single(await verify.PlayerAuthSessions.Where(a => a.StationId == seed.TargetId).ToListAsync());
        Assert.Equal(3, await verify.Set<StationSessionSegment>().CountAsync());
        Assert.Equal(1, await verify.Set<SessionOperation>().CountAsync());
    }

    [PostgresFact]
    public async Task ConcurrentSameExtension_IsChargedOnceAndDifferentPayloadConflicts()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var context1 = database.CreateContext();
        var service1 = Service(context1, clock);
        var game = await service1.StartPaidAsync(Purchase(seed), null, default);
        await using var context2 = database.CreateContext();
        var service2 = Service(context2, clock);
        var operation = Guid.NewGuid();
        await Task.WhenAll(service1.ExtendAsync(game.Id, operation, 30, null, default),
            service2.ExtendAsync(game.Id, operation, 30, null, default));
        var conflict = await Assert.ThrowsAsync<ClubException>(() => service1.ExtendAsync(game.Id, operation, 31, null, default));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        await using var verify = database.CreateContext();
        Assert.Equal(91m, (await verify.Set<Wallet>().SingleAsync()).Balance);
        Assert.Equal(30, (await verify.Set<GamingSession>().SingleAsync()).AddedMinutes);
        Assert.Equal(1, await verify.Set<SessionOperation>().CountAsync());
    }

    [PostgresFact]
    public async Task TransferFailure_RollsBackLocationAuthorizationSegmentsAndJournal()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        await using var context = database.CreateContext();
        var service = Service(context, new MutableClock());
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_transfer_event() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Type" = 'SessionTransferred' THEN RAISE EXCEPTION 'synthetic transfer rollback'; END IF;
            RETURN NEW; END $$;
            CREATE TRIGGER reject_transfer BEFORE INSERT ON session_events FOR EACH ROW EXECUTE FUNCTION reject_transfer_event();
            """);
        Assert.NotNull(await Record.ExceptionAsync(() => service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default)));
        await using var verify = database.CreateContext();
        Assert.Equal(seed.SourceId, (await verify.Set<GamingSession>().SingleAsync()).StationId);
        Assert.Equal(seed.SourceId, (await verify.PlayerAuthSessions.SingleAsync()).StationId);
        Assert.Null((await verify.Set<StationSessionSegment>().SingleAsync()).EndedAtUtc);
        Assert.False(await verify.Set<SessionOperation>().AnyAsync());
    }

    [PostgresFact]
    public async Task TransferRejectsStaleMaintenanceDifferentGroupAndPendingPowerTargets()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("STATION_UNAVAILABLE", (await Assert.ThrowsAsync<ClubException>(() =>
            service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default))).Code);
        await using (var setup = database.CreateContext())
        {
            var target = await setup.Stations.SingleAsync(s => s.Id == seed.TargetId);
            target.UpdateFromAgent(target.Name, target.MachineName, null, "0.1.0", clock.UtcNow);
            target.UpdateClientHealth(true, "Maintenance", clock.UtcNow, clock.UtcNow);
            await setup.SaveChangesAsync();
        }
        Assert.Equal("STATION_UNAVAILABLE", (await Assert.ThrowsAsync<ClubException>(() =>
            service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default))).Code);
        await using (var setup = database.CreateContext())
        {
            var target = await setup.Stations.SingleAsync(s => s.Id == seed.TargetId);
            target.UpdateClientHealth(true, "Available", clock.UtcNow, clock.UtcNow);
            target.AssignGroup(Guid.Parse("10000000-0000-0000-0000-000000000002"));
            await setup.SaveChangesAsync();
        }
        Assert.Equal("STATION_GROUP_MISMATCH", (await Assert.ThrowsAsync<ClubException>(() =>
            service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default))).Code);
        await using (var setup = database.CreateContext())
        {
            (await setup.Stations.SingleAsync(s => s.Id == seed.TargetId)).AssignGroup(seed.GroupId);
            setup.Add(new AgentCommand(Guid.NewGuid(), seed.TargetId, AgentCommandType.RestartStation, null, clock.UtcNow, clock.UtcNow.AddSeconds(30)));
            await setup.SaveChangesAsync();
        }
        Assert.Equal("STATION_POWER_PENDING", (await Assert.ThrowsAsync<ClubException>(() =>
            service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default))).Code);
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<SessionOperation>().AnyAsync());
    }

    [PostgresFact]
    public async Task OperationIdsCannotBeReusedAcrossDepositTransferAndExtension()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        var wallets = new WalletService(new ClubData(context), clock, new WalletPolicy());
        var depositId = Guid.NewGuid();
        await wallets.DepositAsync(seed.UserId, 1m, depositId, null, default);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<ClubException>(() =>
            service.TransferAsync(game.Id, depositId, seed.TargetId, null, default))).Code);
        var transferId = Guid.NewGuid();
        await service.TransferAsync(game.Id, transferId, seed.TargetId, null, default);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<ClubException>(() =>
            wallets.DepositAsync(seed.UserId, 1m, transferId, null, default))).Code);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<ClubException>(() =>
            service.ExtendAsync(game.Id, transferId, 1, null, default))).Code);
    }

    [PostgresFact]
    public async Task SplitPrepaidExtensions_AccumulateRoundingInsteadOfGivingFreeRoundedMinutes()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 0.01m, 1m);
        await using var context = database.CreateContext();
        var service = Service(context, new MutableClock());
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        await service.ExtendAsync(game.Id, Guid.NewGuid(), 14, null, default);
        await service.ExtendAsync(game.Id, Guid.NewGuid(), 15, null, default);
        Assert.Equal(0.01m, (await service.QuoteExtensionAsync(game.Id, 1, default)).Charge);
        await service.ExtendAsync(game.Id, Guid.NewGuid(), 1, null, default);
        await service.EndAsync(game.Id, null, default);
        await using var verify = database.CreateContext();
        Assert.Equal(0.02m, (await verify.Set<GamingSession>().SingleAsync()).FinalPrice);
        Assert.Equal(0.98m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task PostpaidFractionalCentExtension_ReservesEnoughForEveryFundedSecond()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 0.01m, 1m);
        var clock = new MutableClock();
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed, BillingMode.Postpaid, 0.01m), null, default);
        Assert.Equal(0.01m, (await service.QuoteExtensionAsync(game.Id, 1, default)).AdditionalReservation);
        await service.ExtendAsync(game.Id, Guid.NewGuid(), 1, null, default);
        clock.Advance(TimeSpan.FromMinutes(61));
        await service.EndAsync(game.Id, null, default);
        await using var verify = database.CreateContext();
        Assert.Equal(0.01m, (await verify.Set<GamingSession>().SingleAsync()).FinalPrice);
        Assert.Equal(0.02m, (await verify.Set<WalletReservation>().SingleAsync()).Amount);
        Assert.Equal(0.99m, (await verify.Set<Wallet>().SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task Extension_RejectsAuthLifetimeAndHardWindowBeforeAnyCharge()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        Assert.Equal("SESSION_EXCEEDS_AUTH_LIFETIME", (await Assert.ThrowsAsync<ClubException>(() =>
            service.ExtendAsync(game.Id, Guid.NewGuid(), 661, null, default))).Code);
        Assert.Equal(66m, (await service.QuoteExtensionAsync(game.Id, 660, default)).Charge);
        await service.EndAsync(game.Id, null, default);
        var second = await SeedAsync(database, 6m, 100m);
        Guid packageId;
        await using (var setup = database.CreateContext())
        {
            var package = new TariffPackage(Guid.NewGuid(), "Two hour window", second.GroupId, 60, 5m,
                new TimeOnly(12, 0), new TimeOnly(14, 0));
            packageId = package.Id;
            setup.Add(package);
            await setup.SaveChangesAsync();
        }
        var windowGame = await service.StartPaidAsync(new SessionPurchase(Guid.NewGuid(), second.UserId, second.SourceId,
            BillingMode.Prepaid, null, packageId, null, null), null, default);
        Assert.Equal(Now.AddHours(2), (await service.QuoteExtensionAsync(windowGame.Id, 60, default)).ExpectedEndAtUtc);
        Assert.Equal("SESSION_EXCEEDS_PACKAGE_WINDOW", (await Assert.ThrowsAsync<ClubException>(() =>
            service.ExtendAsync(windowGame.Id, Guid.NewGuid(), 61, null, default))).Code);
        await service.PauseAsync(windowGame.Id, null, default);
        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal("SESSION_EXCEEDS_PACKAGE_WINDOW", (await Assert.ThrowsAsync<ClubException>(() =>
            service.ExtendAsync(windowGame.Id, Guid.NewGuid(), 1, null, default))).Code);
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<SessionOperation>().AnyAsync());
    }

    [PostgresFact]
    public async Task ExpiryRecheck_AfterCommittedExtension_DoesNotEndAtOldDeadline()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var context = database.CreateContext();
        var service = Service(context, clock);
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        clock.Advance(TimeSpan.FromMinutes(59));
        await service.ExtendAsync(game.Id, Guid.NewGuid(), 60, null, default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await service.EndAsync(game.Id, null, default, onlyIfDue: true);
        Assert.Equal("Active", result.Status);
        Assert.Equal(3600, result.RemainingSeconds);
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<SessionEvent>().AnyAsync(e => e.Type == SessionEventType.SessionEnded));
    }

    [PostgresFact]
    public async Task ConcurrentTransferAndSourceLogout_NeverOrphanGamingFromItsAuthorization()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        var clock = new MutableClock();
        await using var transferContext = database.CreateContext();
        var transferService = Service(transferContext, clock);
        var game = await transferService.StartPaidAsync(Purchase(seed), null, default);
        await using var logoutContext = database.CreateContext();
        var logoutService = Service(logoutContext, clock);
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => transferService.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default)),
            Record.ExceptionAsync(() => logoutService.LogoutStationAsync(seed.SourceId, seed.AuthId, null, default)));
        Assert.Null(outcomes[1]);
        if (outcomes[0] is { } transferError)
            Assert.Equal("INVALID_SESSION_TRANSITION", Assert.IsType<ClubException>(transferError).Code);
        await using var verify = database.CreateContext();
        var stored = await verify.Set<GamingSession>().SingleAsync();
        var auth = await verify.PlayerAuthSessions.SingleAsync();
        Assert.Equal(stored.StationId, auth.StationId);
        if (outcomes[0] is null)
        {
            Assert.Equal(seed.TargetId, stored.StationId);
            Assert.Equal(GamingSessionStatus.Active, stored.Status);
            Assert.Equal(PlayerAuthSessionStatus.Active, auth.Status);
        }
        else
        {
            Assert.Equal(seed.SourceId, stored.StationId);
            Assert.Equal(GamingSessionStatus.Completed, stored.Status);
            Assert.Equal(PlayerAuthSessionStatus.Ended, auth.Status);
        }
    }

    [PostgresFact]
    public async Task QuoteAndExtension_RespectExplicitNegativeBalancePolicyForPrepaidAndPostpaid()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        foreach (var mode in new[] { BillingMode.Prepaid, BillingMode.Postpaid })
        {
            var seed = await SeedAsync(database, 6m, mode == BillingMode.Prepaid ? 6m : 1m);
            var clock = new MutableClock();
            await using var context = database.CreateContext();
            var service = Service(context, clock, allowNegativeBalance: true);
            var game = await service.StartPaidAsync(Purchase(seed, mode, mode == BillingMode.Postpaid ? 1m : null), null, default);
            var minutes = mode == BillingMode.Prepaid ? 30 : 5;
            var quote = await service.QuoteExtensionAsync(game.Id, minutes, default);
            Assert.Equal(mode == BillingMode.Prepaid ? 3m : 0.5m, quote.Charge + quote.AdditionalReservation);
            await service.ExtendAsync(game.Id, Guid.NewGuid(), minutes, null, default);
            clock.Advance(TimeSpan.FromMinutes(15));
            await service.EndAsync(game.Id, null, default);
            await using var verify = database.CreateContext();
            var wallet = await verify.Set<Wallet>().SingleAsync(w => w.UserId == seed.UserId);
            Assert.Equal(mode == BillingMode.Prepaid ? -3m : -0.5m, wallet.Balance);
        }
    }

    [PostgresFact]
    public async Task TransferRejectsFutureHeartbeatWithoutChangingHistoryOrAuthorization()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database, 6m, 100m);
        await using var context = database.CreateContext();
        var service = Service(context, new MutableClock());
        var game = await service.StartPaidAsync(Purchase(seed), null, default);
        await using (var setup = database.CreateContext())
        {
            var target = await setup.Stations.SingleAsync(s => s.Id == seed.TargetId);
            target.UpdateFromAgent(target.Name, target.MachineName, null, target.AgentVersion, Now.AddSeconds(1));
            await setup.SaveChangesAsync();
        }
        Assert.Equal("STATION_UNAVAILABLE", (await Assert.ThrowsAsync<ClubException>(() =>
            service.TransferAsync(game.Id, Guid.NewGuid(), seed.TargetId, null, default))).Code);
        await using var verify = database.CreateContext();
        Assert.Equal(seed.SourceId, (await verify.Set<GamingSession>().SingleAsync()).StationId);
        Assert.Equal(seed.SourceId, (await verify.PlayerAuthSessions.SingleAsync()).StationId);
        Assert.Equal(1, await verify.Set<StationSessionSegment>().CountAsync());
        Assert.False(await verify.Set<SessionOperation>().AnyAsync());
    }

    private static GamingSessionService Service(GameClubDbContext context, MutableClock clock, Notifications? notifications = null,
        bool allowNegativeBalance = false)
    {
        var data = new ClubData(context);
        return new GamingSessionService(data, clock, notifications ?? new Notifications(),
            new SessionBillingService(data, new WalletService(data, clock, new WalletPolicy { AllowNegativeBalance = allowNegativeBalance }),
                new SessionBillingOptions()));
    }

    private static SessionPurchase Purchase(Seed seed, BillingMode mode = BillingMode.Prepaid, decimal? limit = null) =>
        new(Guid.NewGuid(), seed.UserId, seed.SourceId, mode, seed.TariffId, null, mode == BillingMode.Prepaid ? 60 : null, limit);

    private static async Task<Seed> SeedAsync(PostgresTestDatabase database, decimal rate, decimal balance)
    {
        await using var context = database.CreateContext();
        var source = NewStation();
        var target = NewStation();
        var user = NewUser();
        var tariff = new Tariff(Guid.NewGuid(), "Stage8 hourly", source.StationGroupId!.Value, rate);
        var auth = new PlayerAuthSession(Guid.NewGuid(), user.Id, source.Id, Now, Now.AddHours(12));
        context.AddRange(source, target, user, tariff, auth);
        await context.SaveChangesAsync();
        await new WalletService(new ClubData(context), new MutableClock(), new WalletPolicy())
            .DepositAsync(user.Id, balance, Guid.NewGuid(), null, default);
        return new Seed(user.Id, source.Id, target.Id, tariff.Id, source.StationGroupId.Value, auth.Id);
    }

    private static Station NewStation() => new(Guid.NewGuid(), "Stage8 PC", "STAGE8-" + Guid.NewGuid().ToString("N"), null, "0.1.0", Now);
    private static User NewUser()
    {
        var user = new User(Guid.NewGuid(), "s8_" + Guid.NewGuid().ToString("N")[..12], "Session operations", null, null, Now);
        user.SetPasswordHash("Integration fixture only");
        return user;
    }
    private sealed record Seed(Guid UserId, Guid SourceId, Guid TargetId, Guid TariffId, Guid GroupId, Guid AuthId);
    private sealed class MutableClock : TimeProvider
    {
        public DateTime UtcNow { get; private set; } = Now;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
    private sealed class Notifications : IClubEvents
    {
        public List<Guid> Stations { get; } = [];
        public Task StationChangedAsync(Guid stationId, CancellationToken cancellationToken)
        {
            Stations.Add(stationId);
            return Task.CompletedTask;
        }
    }
}
