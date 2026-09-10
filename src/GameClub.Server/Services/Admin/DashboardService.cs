using System.Data;
using GameClub.Application.Gaming;
using GameClub.Contracts.Gaming;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Stations;
using GameClub.Domain.Users;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services.Admin;

public sealed record DashboardUser(Guid Id, string Username, string? DisplayName);
public sealed record DashboardStation(Guid Id, string Name, Guid? StationGroupId, string? GroupName,
    string Status, bool AgentOnline, bool ClientConnected, string ClientState, string AgentVersion,
    DateTime LastSeenAtUtc, DashboardUser? CurrentUser, GamingSessionSnapshot? GamingSession, string? TariffName);
public sealed record DashboardResponse(DateTime ServerTimeUtc, IReadOnlyList<DashboardStation> Stations);

public sealed class DashboardService(GameClubDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(30);

    public async Task<DashboardResponse> GetAsync(CancellationToken ct)
    {
        // All component projections describe one PostgreSQL snapshot, without tracking or mutating entities.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var now = clock.GetUtcNow().UtcDateTime;
        var stations = await db.Stations.AsNoTracking().OrderBy(s => s.Name).ThenBy(s => s.Id).Select(s => new
        {
            s.Id, s.Name, s.StationGroupId, s.Status, s.AgentVersion, s.LastSeenAtUtc,
            s.ClientConnected, s.ClientState, s.ClientLastSeenAtUtc,
            GroupName = db.Set<StationGroup>().Where(g => g.Id == s.StationGroupId).Select(g => g.Name).FirstOrDefault()
        }).ToListAsync(ct);
        var users = await db.PlayerAuthSessions.AsNoTracking().Where(a =>
            a.Status == PlayerAuthSessionStatus.Active && a.ExpiresAtUtc > now && a.User.Status == UserStatus.Active)
            .Select(a => new { a.StationId, User = new DashboardUser(a.UserId, a.User.Username, a.User.DisplayName) })
            .ToDictionaryAsync(a => a.StationId, a => a.User, ct);
        var games = await db.Set<GamingSession>().AsNoTracking().Where(s =>
            s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused).Select(s => new
            {
                Session = s,
                TariffName = db.Set<Tariff>().Where(t => t.Id == s.TariffId).Select(t => t.Name).FirstOrDefault() ??
                             db.Set<TariffPackage>().Where(p => p.Id == s.PackageId).Select(p => p.Name).FirstOrDefault()
            }).ToDictionaryAsync(s => s.Session.StationId, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        var result = stations.Select(station =>
        {
            var agentOnline = station.Status == StationStatus.Online && station.LastSeenAtUtc <= now &&
                              now - station.LastSeenAtUtc <= AgentTimeout;
            var clientConnected = agentOnline && station.ClientConnected &&
                                  station.ClientLastSeenAtUtc is { } lastClient && lastClient <= now &&
                                  now - lastClient <= Station.ClientHeartbeatTimeout;
            users.TryGetValue(station.Id, out var user);
            games.TryGetValue(station.Id, out var game);
            var validGame = game is not null && user?.Id == game.Session.UserId && !game.Session.IsExpiredAt(now);
            return new DashboardStation(station.Id, station.Name, station.StationGroupId, station.GroupName,
                station.Status == StationStatus.Online && !agentOnline ? "Offline" : station.Status.ToString(),
                agentOnline, clientConnected, clientConnected ? station.ClientState : "Offline", station.AgentVersion,
                station.LastSeenAtUtc, user, validGame ? GamingSessionService.Snapshot(game!.Session, now) : null,
                validGame ? game!.TariffName : null);
        }).ToList();
        return new DashboardResponse(now, result);
    }
}
