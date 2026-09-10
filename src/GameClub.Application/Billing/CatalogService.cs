using System.Globalization;
using GameClub.Application.Abstractions;
using GameClub.Domain.Billing;
using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Billing;

public sealed class CatalogService(IClubData data, TimeProvider clock)
{
    public Task<StationGroup> CreateGroupAsync(string name, Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            var group = new StationGroup(Guid.NewGuid(), name);
            if (await data.Query<StationGroup>().AnyAsync(g => g.NormalizedName == group.NormalizedName, token))
                throw new ClubException("GROUP_ALREADY_EXISTS");
            data.Add(group);
            Audit("StationGroupCreated", $"GroupId={group.Id:D}", employeeId);
            return group;
        }, ct);

    public Task<Tariff> CreateTariffAsync(string name, Guid stationGroupId, decimal hourlyPrice,
        Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            await RequireGroupAsync(stationGroupId, token);
            var tariff = new Tariff(Guid.NewGuid(), name, stationGroupId, hourlyPrice);
            data.Add(tariff);
            Audit("TariffCreated", $"TariffId={tariff.Id:D};GroupId={stationGroupId:D};Price={Price(hourlyPrice)}", employeeId);
            return tariff;
        }, ct);

    public Task<TariffPackage> CreatePackageAsync(string name, Guid stationGroupId, int durationMinutes,
        decimal price, TimeOnly? availableFrom, TimeOnly? availableUntil, Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            await RequireGroupAsync(stationGroupId, token);
            var package = new TariffPackage(Guid.NewGuid(), name, stationGroupId, durationMinutes,
                price, availableFrom, availableUntil);
            data.Add(package);
            Audit("TariffPackageCreated", $"PackageId={package.Id:D};GroupId={stationGroupId:D};Price={Price(price)}", employeeId);
            return package;
        }, ct);

    public Task<Tariff> UpdateTariffPriceAsync(Guid id, decimal price, Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            var tariff = await RequiredTariffAsync(id, token);
            var oldPrice = tariff.HourlyPrice;
            tariff.UpdatePrice(price);
            Audit("TariffPriceChanged", $"TariffId={id:D};OldPrice={Price(oldPrice)};NewPrice={Price(price)}", employeeId);
            return tariff;
        }, ct);

    public Task<Tariff> SetTariffActiveAsync(Guid id, bool active, Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            var tariff = await RequiredTariffAsync(id, token);
            tariff.SetActive(active);
            Audit("TariffActiveChanged", $"TariffId={id:D};IsActive={active}", employeeId);
            return tariff;
        }, ct);

    public Task<TariffPackage> SetPackageActiveAsync(Guid id, bool active, Guid? employeeId, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            var package = await data.Query<TariffPackage>().SingleOrDefaultAsync(p => p.Id == id, token)
                ?? throw new ClubException("PACKAGE_NOT_FOUND");
            package.SetActive(active);
            Audit("TariffPackageActiveChanged", $"PackageId={id:D};IsActive={active}", employeeId);
            return package;
        }, ct);

    private async Task RequireGroupAsync(Guid id, CancellationToken ct)
    {
        if (!await data.Query<StationGroup>().AnyAsync(group => group.Id == id, ct))
            throw new ClubException("GROUP_NOT_FOUND");
    }

    private async Task<Tariff> RequiredTariffAsync(Guid id, CancellationToken ct) =>
        await data.Query<Tariff>().SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new ClubException("TARIFF_NOT_FOUND");

    private void Audit(string eventType, string details, Guid? employeeId) =>
        data.Add(new SecurityAuditEvent(Guid.NewGuid(), eventType, null,
            clock.GetUtcNow().UtcDateTime, null, $"{details};EmployeeId={employeeId:D}"));

    private static string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
