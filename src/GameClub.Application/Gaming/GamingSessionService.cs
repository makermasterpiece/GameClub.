using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GameClub.Application.Abstractions;
using GameClub.Application.Billing;
using GameClub.Contracts.Gaming;
using GameClub.Domain.Gaming;
using GameClub.Domain.Pos;
using GameClub.Domain.Commands;
using GameClub.Domain.Billing;
using GameClub.Domain.Security;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Gaming;

public sealed class GamingSessionService(IClubData data, TimeProvider clock, IClubEvents events,
    SessionBillingService? billing = null)
{
    public async Task<GamingSessionSnapshot> StartPaidAsync(SessionPurchase purchase, Guid? employeeId, CancellationToken ct)
    {
        var billingService = billing ?? throw new InvalidOperationException("Session billing is not configured.");
        var fingerprint = SessionBillingService.Fingerprint(purchase);
        var result = await AtomicPurchaseAsync(async token =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await data.Query<GamingSession>().SingleOrDefaultAsync(s => s.Id == purchase.OperationId, token);
            if (existing is not null)
            {
                if (existing.PurchaseFingerprint != fingerprint) throw new ClubException("IDEMPOTENCY_CONFLICT");
                return Snapshot(existing, now);
            }
            if (await data.Query<WalletTransaction>().AnyAsync(t => t.OperationId == purchase.OperationId, token) ||
                await data.Query<SessionOperation>().AnyAsync(o => o.Id == purchase.OperationId, token) ||
                await data.Query<PosOperation>().AnyAsync(o => o.Id == purchase.OperationId, token))
                throw new ClubException("IDEMPOTENCY_CONFLICT");

            var station = await data.Query<Station>().SingleOrDefaultAsync(s => s.Id == purchase.StationId, token);
            await RequireAvailableStationAsync(station, now, token);
            var auth = await data.Query<PlayerAuthSession>().SingleOrDefaultAsync(a => a.UserId == purchase.UserId &&
                    a.StationId == purchase.StationId && a.Status == PlayerAuthSessionStatus.Active &&
                    a.ExpiresAtUtc > now && a.User.Status == UserStatus.Active, token)
                ?? throw new ClubException("PLAYER_NOT_AUTHENTICATED");
            if (await data.Query<GamingSession>().AnyAsync(s => (s.UserId == purchase.UserId || s.StationId == purchase.StationId) &&
                    (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused), token))
                throw new ClubException("SESSION_ALREADY_ACTIVE");

            var session = await billingService.PurchaseCoreAsync(purchase, station!, now, auth.ExpiresAtUtc, employeeId, token);
            session.Start(now);
            StartSegment(session, now);
            AddEvent(session, SessionEventType.SessionStarted, now, employeeId);
            return Snapshot(session, now);
        }, ct);
        await events.StationChangedAsync(purchase.StationId, ct);
        return result;
    }

    public async Task<GamingSessionSnapshot> StartAsync(Guid userId, Guid stationId, int? minutes,
        Guid? employeeId, CancellationToken ct)
    {
        var result = await data.AtomicAsync(async token =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var station = await data.Query<Station>().SingleOrDefaultAsync(s => s.Id == stationId, token);
            await RequireAvailableStationAsync(station, now, token);
            if (!await data.Query<PlayerAuthSession>().AnyAsync(s => s.UserId == userId && s.StationId == stationId &&
                    s.Status == PlayerAuthSessionStatus.Active && s.ExpiresAtUtc > now && s.User.Status == UserStatus.Active, token))
                throw new ClubException("PLAYER_NOT_AUTHENTICATED");
            if (await data.Query<GamingSession>().AnyAsync(s => (s.StationId == stationId || s.UserId == userId) &&
                    (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused), token))
                throw new ClubException("SESSION_ALREADY_ACTIVE");
            var session = new GamingSession(Guid.NewGuid(), userId, stationId, minutes, now);
            session.Start(now);
            data.Add(session);
            StartSegment(session, now);
            AddEvent(session, SessionEventType.SessionStarted, now, employeeId);
            return Snapshot(session, now);
        }, ct);
        await events.StationChangedAsync(stationId, ct);
        return result;
    }

    public Task<GamingSessionSnapshot> PauseAsync(Guid id, Guid? employeeId, CancellationToken ct) =>
        ChangeAsync(id, SessionEventType.SessionPaused, (s, now) => s.Pause(now), employeeId, ct);

    public Task<GamingSessionSnapshot> ResumeAsync(Guid id, Guid? employeeId, CancellationToken ct) =>
        ChangeAsync(id, SessionEventType.SessionResumed, (s, now) => s.Resume(now), employeeId, ct);

    public Task<SessionExtensionQuote> QuoteExtensionAsync(Guid sessionId, int minutes, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            var session = await RequiredAsync(sessionId, token);
            var now = clock.GetUtcNow().UtcDateTime;
            var auth = await RequireMutableSessionAsync(session, now, token);
            return await RequiredBilling().QuoteExtensionCoreAsync(session, minutes, now, auth.ExpiresAtUtc, token);
        }, ct);

    public async Task<GamingSessionSnapshot> ExtendAsync(Guid sessionId, Guid operationId, int minutes,
        Guid? employeeId, CancellationToken ct)
    {
        var fingerprint = OperationFingerprint(sessionId, operationId, SessionOperationType.Extend,
            minutes.ToString(CultureInfo.InvariantCulture), employeeId);
        var result = await AtomicPurchaseAsync(async token =>
        {
            var session = await RequiredAsync(sessionId, token);
            var now = clock.GetUtcNow().UtcDateTime;
            if (await IsOperationReplayAsync(operationId, fingerprint, token)) return Snapshot(session, now);
            var auth = await RequireMutableSessionAsync(session, now, token);
            var quote = await RequiredBilling().QuoteExtensionCoreAsync(session, minutes, now, auth.ExpiresAtUtc, token);
            data.Add(new SessionOperation(operationId, session.Id, SessionOperationType.Extend, fingerprint, now, employeeId));
            // Make ownership visible to the wallet core in this same transaction before creating its ledger entry.
            await data.SaveAsync(token);
            await RequiredBilling().ExtendCoreAsync(session, operationId, quote, now, employeeId, token);
            var details = FormattableString.Invariant($"OperationId={operationId:D};Minutes={minutes};Charge={quote.Charge};AdditionalReservation={quote.AdditionalReservation}");
            AddEvent(session, SessionEventType.SessionExtended, now, employeeId, details);
            AddOperationAudit(session, "SessionExtended", operationId, employeeId, now, details);
            return Snapshot(session, now);
        }, ct);
        await events.StationChangedAsync(result.StationId, ct);
        return result;
    }

    public async Task<GamingSessionSnapshot> TransferAsync(Guid sessionId, Guid operationId, Guid destinationStationId,
        Guid? employeeId, CancellationToken ct)
    {
        if (destinationStationId == Guid.Empty) throw new ClubException("INVALID_STATION_ID");
        var fingerprint = OperationFingerprint(sessionId, operationId, SessionOperationType.Transfer,
            destinationStationId.ToString("D"), employeeId);
        var result = await AtomicPurchaseAsync(async token =>
        {
            var session = await RequiredAsync(sessionId, token);
            var now = clock.GetUtcNow().UtcDateTime;
            if (await IsOperationReplayAsync(operationId, fingerprint, token))
                return (Snapshot: Snapshot(session, now), Source: (Guid?)null);
            var auth = await RequireMutableSessionAsync(session, now, token);
            if (session.StationId == destinationStationId) throw new ClubException("SAME_STATION");
            var source = await data.Query<Station>().SingleAsync(s => s.Id == session.StationId, token);
            var destination = await data.Query<Station>().SingleOrDefaultAsync(s => s.Id == destinationStationId, token);
            await RequireAvailableStationAsync(destination, now, token);
            if (source.StationGroupId is null || source.StationGroupId != destination!.StationGroupId)
                throw new ClubException("STATION_GROUP_MISMATCH");
            if (session.TariffId is { } tariffId && !await data.Query<Tariff>().AnyAsync(t =>
                    t.Id == tariffId && t.StationGroupId == destination.StationGroupId, token) ||
                session.PackageId is { } packageId && !await data.Query<TariffPackage>().AnyAsync(p =>
                    p.Id == packageId && p.StationGroupId == destination.StationGroupId, token))
                throw new ClubException("STATION_GROUP_MISMATCH");
            if (await data.Query<GamingSession>().AnyAsync(s => s.StationId == destinationStationId &&
                    (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused), token) ||
                await data.Query<PlayerAuthSession>().AnyAsync(a => a.StationId == destinationStationId &&
                    a.Status == PlayerAuthSessionStatus.Active, token))
                throw new ClubException("STATION_OCCUPIED");

            var sourceId = session.StationId;
            await EndSegmentAsync(session, now, token);
            // Close before inserting the next segment because the open-segment index is unique.
            await data.SaveAsync(token);
            session.Transfer(destinationStationId, now);
            auth.Transfer(destinationStationId, now);
            StartSegment(session, now);
            data.Add(new SessionOperation(operationId, session.Id, SessionOperationType.Transfer, fingerprint, now, employeeId));
            var details = $"OperationId={operationId:D};SourceStationId={sourceId:D};DestinationStationId={destinationStationId:D}";
            AddEvent(session, SessionEventType.SessionTransferred, now, employeeId, details);
            AddOperationAudit(session, "SessionTransferred", operationId, employeeId, now, details);
            return (Snapshot: Snapshot(session, now), Source: (Guid?)sourceId);
        }, ct);
        if (result.Source is { } sourceId)
        {
            await events.StationChangedAsync(sourceId, ct);
            await events.StationChangedAsync(destinationStationId, ct);
        }
        return result.Snapshot;
    }

    public async Task<GamingSessionSnapshot> EndAsync(Guid id, Guid? employeeId, CancellationToken ct, bool onlyIfDue = false)
    {
        var result = await data.AtomicAsync(async token =>
        {
            var session = await RequiredAsync(id, token);
            var now = clock.GetUtcNow().UtcDateTime;
            if (onlyIfDue && !await IsDueAsync(session, now, token)) return Snapshot(session, now);
            await EndCoreAsync(session, now, employeeId, token);
            return Snapshot(session, now);
        }, ct);
        await events.StationChangedAsync(result.StationId, ct);
        return result;
    }

    public async Task<StationGamingState> CurrentAsync(Guid stationId, CancellationToken ct)
    {
        var result = await data.AtomicAsync(async token =>
        {
            var session = await data.Query<GamingSession>().SingleOrDefaultAsync(s =>
                s.StationId == stationId && (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused), token);
            var now = clock.GetUtcNow().UtcDateTime;
            var ended = session is not null && await IsDueAsync(session, now, token);
            if (ended)
            {
                await EndCoreAsync(session!, now, null, token);
                session = null;
            }

            return (State: new StationGamingState(session is null ? null : Snapshot(session, now), now), Ended: ended);
        }, ct);
        if (result.Ended) await events.StationChangedAsync(stationId, ct);
        return result.State;
    }

    public Task LogoutStationAsync(Guid stationId, string? sourceIp, CancellationToken ct) =>
        LogoutStationAsync(stationId, null, sourceIp, ct);

    public async Task LogoutStationAsync(Guid stationId, Guid? expectedAuthSessionId, string? sourceIp, CancellationToken ct)
    {
        await data.AtomicAsync(async token =>
        {
            if (expectedAuthSessionId is { } expected && !await data.Query<PlayerAuthSession>().AnyAsync(a =>
                    a.Id == expected && a.StationId == stationId && a.Status == PlayerAuthSessionStatus.Active, token))
                return false;
            var sessions = await data.Query<GamingSession>().Where(s => s.StationId == stationId &&
                (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused)).ToListAsync(token);
            var authSessions = await data.Query<PlayerAuthSession>().Where(s => s.StationId == stationId &&
                s.Status == PlayerAuthSessionStatus.Active).ToListAsync(token);
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var session in sessions)
            {
                if (!session.End(now)) continue;
                await EndSegmentAsync(session, session.EndedAtUtc!.Value, token);
                await SettleBillingAsync(session, null, token);
                AddEvent(session, SessionEventType.SessionEnded, now, null);
            }

            foreach (var auth in authSessions)
            {
                if (!auth.End(now)) continue;
                data.Add(new SecurityAuditEvent(Guid.NewGuid(), "PlayerLogout", stationId, now, sourceIp,
                    $"SessionId={auth.Id:D};Result=Success", auth.UserId));
            }

            return true;
        }, ct);
        await events.StationChangedAsync(stationId, ct);
    }

    public async Task<int> CompleteDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var validAuth = data.Query<PlayerAuthSession>().Where(a => a.Status == PlayerAuthSessionStatus.Active &&
            a.ExpiresAtUtc > now && a.User.Status == UserStatus.Active);
        var ids = await data.Query<GamingSession>().AsNoTracking().Where(s =>
            (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused) &&
            ((s.Status == GamingSessionStatus.Active && s.ExpectedEndAtUtc <= now) || s.WindowEndsAtUtc <= now ||
             !validAuth.Any(a => a.StationId == s.StationId && a.UserId == s.UserId)))
            .OrderBy(s => s.CreatedAtUtc).Select(s => s.Id).Take(100).ToListAsync(ct);
        foreach (var id in ids) await EndAsync(id, null, ct, onlyIfDue: true);
        return ids.Count;
    }

    private async Task<GamingSessionSnapshot> ChangeAsync(Guid id, SessionEventType type,
        Action<GamingSession, DateTime> change, Guid? employeeId, CancellationToken ct)
    {
        var result = await data.AtomicAsync(async token =>
        {
            var session = await RequiredAsync(id, token);
            var now = clock.GetUtcNow().UtcDateTime;
            if (session.IsExpiredAt(now)) throw new ClubException("SESSION_EXPIRED");
            if (!await HasValidAuthAsync(session, now, token)) throw new ClubException("PLAYER_NOT_AUTHENTICATED");
            try { change(session, now); }
            catch (InvalidOperationException) { throw new ClubException("INVALID_SESSION_TRANSITION"); }
            AddEvent(session, type, now, employeeId);
            return Snapshot(session, now);
        }, ct);
        await events.StationChangedAsync(result.StationId, ct);
        return result;
    }

    private async Task<GamingSession> RequiredAsync(Guid id, CancellationToken ct) =>
        await data.Query<GamingSession>().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new ClubException("SESSION_NOT_FOUND");

    private Task<bool> HasValidAuthAsync(GamingSession session, DateTime now, CancellationToken ct) =>
        data.Query<PlayerAuthSession>().AnyAsync(a => a.StationId == session.StationId && a.UserId == session.UserId &&
            a.Status == PlayerAuthSessionStatus.Active && a.ExpiresAtUtc > now && a.User.Status == UserStatus.Active, ct);

    private async Task<bool> IsDueAsync(GamingSession session, DateTime now, CancellationToken ct) =>
        session.Status is GamingSessionStatus.Active or GamingSessionStatus.Paused &&
        (session.IsExpiredAt(now) || !await HasValidAuthAsync(session, now, ct));

    private async Task EndCoreAsync(GamingSession session, DateTime now, Guid? employeeId, CancellationToken ct)
    {
        if (!session.End(now)) return;
        await EndSegmentAsync(session, session.EndedAtUtc!.Value, ct);
        await SettleBillingAsync(session, employeeId, ct);
        AddEvent(session, SessionEventType.SessionEnded, now, employeeId);
        var auth = await data.Query<PlayerAuthSession>().Where(s => s.StationId == session.StationId &&
            s.UserId == session.UserId && s.Status == PlayerAuthSessionStatus.Active).ToListAsync(ct);
        foreach (var item in auth) item.End(now);
    }

    private Task SettleBillingAsync(GamingSession session, Guid? employeeId, CancellationToken ct) =>
        session.BillingMode is null ? Task.CompletedTask :
            (billing ?? throw new InvalidOperationException("Session billing is not configured."))
            .SettleCoreAsync(session, employeeId, ct);

    private async Task<T> AtomicPurchaseAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await data.AtomicAsync(operation, ct); }
            catch (ClubException ex) when (ex.Code == "CONCURRENT_CONFLICT" && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct);
            }
        }
    }

    private void AddEvent(GamingSession session, SessionEventType type, DateTime now, Guid? employeeId, string? details = null) =>
        data.Add(new SessionEvent(Guid.NewGuid(), session.Id, type, now, details, employeeId));

    private SessionBillingService RequiredBilling() => billing ?? throw new InvalidOperationException("Session billing is not configured.");

    private async Task<PlayerAuthSession> RequireMutableSessionAsync(GamingSession session, DateTime now, CancellationToken ct)
    {
        if (session.Status is not (GamingSessionStatus.Active or GamingSessionStatus.Paused))
            throw new ClubException("INVALID_SESSION_TRANSITION");
        if (session.IsExpiredAt(now)) throw new ClubException("SESSION_EXPIRED");
        return await data.Query<PlayerAuthSession>().SingleOrDefaultAsync(a => a.StationId == session.StationId &&
            a.UserId == session.UserId && a.Status == PlayerAuthSessionStatus.Active && a.ExpiresAtUtc > now &&
            a.User.Status == UserStatus.Active, ct) ?? throw new ClubException("PLAYER_NOT_AUTHENTICATED");
    }

    private async Task RequireAvailableStationAsync(Station? station, DateTime now, CancellationToken ct)
    {
        if (station?.Status != StationStatus.Online || station.LastSeenAtUtc > now ||
            station.LastSeenAtUtc < now - StationPowerPolicy.HeartbeatFreshness ||
            station.ClientState == "Maintenance") throw new ClubException("STATION_UNAVAILABLE");
        var powerCutoff = now - StationPowerPolicy.BusyGrace;
        if (await data.Query<AgentCommand>().AnyAsync(c => c.StationId == station.Id &&
                (c.Type == AgentCommandType.RestartStation || c.Type == AgentCommandType.ShutdownStation) &&
                c.Status != AgentCommandStatus.Failed && c.Status != AgentCommandStatus.Expired && c.ExpiresAtUtc > powerCutoff, ct))
            throw new ClubException("STATION_POWER_PENDING");
    }

    private async Task<bool> IsOperationReplayAsync(Guid operationId, string fingerprint, CancellationToken ct)
    {
        var operation = await data.Query<SessionOperation>().SingleOrDefaultAsync(o => o.Id == operationId, ct);
        if (operation is not null)
        {
            if (operation.Fingerprint != fingerprint) throw new ClubException("IDEMPOTENCY_CONFLICT");
            return true;
        }
        if (await data.Query<GamingSession>().AnyAsync(s => s.Id == operationId, ct) ||
            await data.Query<WalletTransaction>().AnyAsync(t => t.OperationId == operationId, ct) ||
            await data.Query<PosOperation>().AnyAsync(o => o.Id == operationId, ct))
            throw new ClubException("IDEMPOTENCY_CONFLICT");
        return false;
    }

    private static string OperationFingerprint(Guid sessionId, Guid operationId, SessionOperationType type, string payload, Guid? employeeId)
    {
        if (sessionId == Guid.Empty || operationId == Guid.Empty || employeeId == Guid.Empty)
            throw new ClubException("INVALID_OPERATION_ID");
        var canonical = $"{sessionId:D}|{type}|{payload}|{employeeId?.ToString("D") ?? "-"}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private void StartSegment(GamingSession session, DateTime now) =>
        data.Add(new StationSessionSegment(Guid.NewGuid(), session.Id, session.StationId, now));

    private async Task EndSegmentAsync(GamingSession session, DateTime endedAtUtc, CancellationToken ct)
    {
        var segment = await data.Query<StationSessionSegment>().SingleOrDefaultAsync(s =>
            s.GamingSessionId == session.Id && s.EndedAtUtc == null, ct);
        if (segment is null && session.StartedAtUtc is { } started)
        {
            // Supports pre-Stage8 rows when an operator has not backfilled their history yet.
            segment = new StationSessionSegment(Guid.NewGuid(), session.Id, session.StationId, started);
            data.Add(segment);
        }
        segment?.End(endedAtUtc);
    }

    private void AddOperationAudit(GamingSession session, string type, Guid operationId, Guid? employeeId, DateTime now, string details) =>
        data.Add(new SecurityAuditEvent(Guid.NewGuid(), type, session.StationId, now, null,
            $"EmployeeId={employeeId?.ToString("D") ?? "system"};{details};Result=Success", session.UserId));

    public static GamingSessionSnapshot Snapshot(GamingSession session, DateTime now) => new(
        session.Id, session.UserId, session.StationId, session.Status.ToString(), session.StartedAtUtc,
        session.ExpectedEndAtUtc, session.RemainingSeconds(now), now, session.ElapsedSeconds(now), session.TariffId, session.PackageId);
}
