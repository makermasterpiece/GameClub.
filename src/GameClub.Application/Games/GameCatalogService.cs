using GameClub.Application.Abstractions;
using GameClub.Contracts.Games;
using GameClub.Domain.Games;
using GameClub.Domain.Security;
using GameClub.Domain.Stations;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Games;

public sealed class GameCatalogService(IClubData data, TimeProvider clock)
{
    public const int MaximumInventoryEntries = 500;

    public Task<Game> SaveAsync(Guid? id, string name, string? playniteGameId, string? executable,
        string? coverUrl, bool isActive, Guid employeeId, CancellationToken ct) => data.AtomicAsync(async token =>
    {
        if (employeeId == Guid.Empty) throw new ClubException("EMPLOYEE_REQUIRED");
        var candidate = new Game(id ?? Guid.NewGuid(), name, playniteGameId, executable, coverUrl, isActive);
        if (candidate.PlayniteGameId is not null && await data.Query<Game>().AnyAsync(g =>
                g.PlayniteGameId == candidate.PlayniteGameId && g.Id != candidate.Id, token))
            throw new ClubException("PLAYNITE_GAME_ALREADY_MAPPED");
        var game = id is null ? candidate : await data.Query<Game>().SingleOrDefaultAsync(g => g.Id == id, token)
            ?? throw new ClubException("GAME_NOT_FOUND");
        if (id is null) data.Add(game);
        else
        {
            if (game.PlayniteGameId != candidate.PlayniteGameId)
            {
                // A catalogue remap must not reuse an old report as proof that the new game is installed.
                var reports = await data.Query<StationGame>().Where(g => g.GameId == game.Id).ToListAsync(token);
                foreach (var report in reports) report.Report(false, clock.GetUtcNow().UtcDateTime);
            }
            game.Update(candidate.Name, candidate.PlayniteGameId, candidate.Executable, candidate.CoverUrl, candidate.IsActive);
        }
        data.Add(new SecurityAuditEvent(Guid.NewGuid(), id is null ? "GameCreated" : "GameUpdated", null,
            clock.GetUtcNow().UtcDateTime, null, $"GameId={game.Id:D};EmployeeId={employeeId:D};IsActive={game.IsActive}"));
        return game;
    }, ct);

    public Task ReportInventoryAsync(Guid stationId, IReadOnlyList<StationGameInventoryEntry>? games, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            if (games is null || games.Count > MaximumInventoryEntries || games.Any(g => g is null ||
                    g.GameId == Guid.Empty || g.PlayniteGameId == Guid.Empty) ||
                games.Select(g => g.GameId).Distinct().Count() != games.Count ||
                games.Select(g => g.PlayniteGameId).Distinct().Count() != games.Count)
                throw new ClubException("INVALID_GAME_INVENTORY");
            if (!await data.Query<Station>().AnyAsync(s => s.Id == stationId, token))
                throw new ClubException("STATION_NOT_FOUND");
            var ids = games.Select(g => g.GameId).ToArray();
            var known = await data.Query<Game>().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, token);
            if (known.Count != games.Count) throw new ClubException("UNKNOWN_GAME");
            foreach (var item in games)
                if (known[item.GameId].PlayniteGameId != item.PlayniteGameId.ToString("D"))
                    throw new ClubException("PLAYNITE_GAME_MISMATCH");
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await data.Query<StationGame>().Where(g => g.StationId == stationId).ToDictionaryAsync(g => g.GameId, token);
            foreach (var report in existing.Values) report.Report(false, now);
            foreach (var item in games)
            {
                if (existing.TryGetValue(item.GameId, out var report)) report.Report(item.Installed, now);
                else data.Add(new StationGame(stationId, item.GameId, item.Installed, now));
            }
            return true;
        }, ct);
}
