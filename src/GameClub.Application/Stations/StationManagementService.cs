using GameClub.Application.Abstractions;
using GameClub.Domain.Billing;
using GameClub.Domain.Gaming;
using GameClub.Domain.Security;
using GameClub.Domain.Stations;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Stations;

public sealed class StationManagementService(IClubData data, TimeProvider clock)
{
    public Task<Guid> AssignGroupAsync(Guid stationId, Guid groupId, Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            if (!await data.Query<StationGroup>().AnyAsync(g => g.Id == groupId, token)) throw new ClubException("GROUP_NOT_FOUND");
            var station = await data.Query<Station>().SingleOrDefaultAsync(s => s.Id == stationId, token)
                ?? throw new ClubException("STATION_NOT_FOUND");
            if (await data.Query<GamingSession>().AnyAsync(s => s.StationId == stationId &&
                (s.Status == GamingSessionStatus.Active || s.Status == GamingSessionStatus.Paused), token))
                throw new ClubException("STATION_OCCUPIED");
            station.AssignGroup(groupId);
            data.Add(new SecurityAuditEvent(Guid.NewGuid(), "StationGroupChanged", stationId, clock.GetUtcNow().UtcDateTime,
                null, $"GroupId={groupId};EmployeeId={employeeId}"));
            return stationId;
        }, ct);
}
