using GameClub.Application.Abstractions;
using GameClub.Domain.Billing;
using GameClub.Domain.Employees;
using GameClub.Domain.Gaming;
using GameClub.Domain.Pos;
using GameClub.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Application.Pos;

public sealed record ShiftSummary(EmployeeShift Shift, decimal GamingSales, decimal ProductSales, decimal Refunds,
    decimal Cash, decimal Card, decimal Wallet, decimal ExpectedCash, decimal? CashDifference);

public sealed class ShiftService(IClubData data, TimeProvider clock)
{
    public Task<EmployeeShift?> CurrentAsync(Guid employeeId, CancellationToken ct) => data.Query<EmployeeShift>().AsNoTracking().SingleOrDefaultAsync(x => x.EmployeeId == employeeId && x.ClosedAtUtc == null, ct);
    public Task<List<EmployeeShift>> ListAsync(Guid employeeId, CancellationToken ct) => data.Query<EmployeeShift>().AsNoTracking().Where(x => x.EmployeeId == employeeId).OrderByDescending(x => x.OpenedAtUtc).Take(50).ToListAsync(ct);

    public Task<EmployeeShift> OpenAsync(Guid operationId, decimal openingCash, Guid employeeId, CancellationToken ct) => PosAtomic.RunAsync(data, async token =>
    {
        var shift = new EmployeeShift(operationId, employeeId, clock.GetUtcNow().UtcDateTime, openingCash);
        var fingerprint = PosFingerprint.Create(new { employeeId, openingCash });
        var previous = await FindOperation(operationId, "ShiftOpen", fingerprint, token);
        if (previous != null) return await data.Query<EmployeeShift>().SingleAsync(x => x.Id == previous.EntityId, token);
        if (!await data.Query<Employee>().AnyAsync(x => x.Id == employeeId && x.IsActive, token)) throw new ClubException("EMPLOYEE_NOT_FOUND");
        if (await data.Query<EmployeeShift>().AnyAsync(x => x.EmployeeId == employeeId && x.ClosedAtUtc == null, token)) throw new ClubException("SHIFT_ALREADY_OPEN");
        data.Add(shift); data.Add(new PosOperation(operationId, "ShiftOpen", fingerprint, shift.Id)); Audit("EmployeeShiftOpened", shift.Id, employeeId);
        return shift;
    }, ct);

    public Task<ShiftSummary> CloseAsync(Guid shiftId, Guid operationId, decimal closingCash, Guid employeeId, CancellationToken ct) => PosAtomic.RunAsync(data, async token =>
    {
        if (operationId == Guid.Empty || employeeId == Guid.Empty || Money.RequireExactScale(closingCash) < 0) throw new ArgumentException("Invalid shift close.");
        var fingerprint = PosFingerprint.Create(new { shiftId, employeeId, closingCash });
        var previous = await FindOperation(operationId, "ShiftClose", fingerprint, token);
        var shift = await data.Query<EmployeeShift>().SingleOrDefaultAsync(x => x.Id == shiftId, token) ?? throw new ClubException("SHIFT_NOT_FOUND");
        if (shift.EmployeeId != employeeId) throw new ClubException("SHIFT_NOT_OWNED");
        if (previous == null)
        {
            if (shift.ClosedAtUtc != null) throw new ClubException("SHIFT_CLOSED");
            var now = clock.GetUtcNow().UtcDateTime;
            shift.Close(closingCash, operationId, fingerprint, now, await GamingTotal(shift, now, token));
            data.Add(new PosOperation(operationId, "ShiftClose", fingerprint, shiftId)); Audit("EmployeeShiftClosed", shiftId, employeeId);
        }
        return await Summarize(shift, token);
    }, ct);

    public Task<ShiftSummary> SummaryAsync(Guid shiftId, Guid requestingEmployee, bool allowOther, CancellationToken ct) => data.AtomicAsync(async token =>
    {
        var shift = await data.Query<EmployeeShift>().SingleOrDefaultAsync(x => x.Id == shiftId, token) ?? throw new ClubException("SHIFT_NOT_FOUND");
        if (requestingEmployee == Guid.Empty || !allowOther && shift.EmployeeId != requestingEmployee) throw new ClubException("SHIFT_NOT_OWNED");
        return await Summarize(shift, token);
    }, ct);

    private async Task<ShiftSummary> Summarize(EmployeeShift shift, CancellationToken ct)
    {
        var sales = await data.Query<Sale>().Where(x => x.EmployeeShiftId == shift.Id).ToListAsync(ct);
        var refunds = await (from refund in data.Query<SaleRefund>() join sale in data.Query<Sale>() on refund.SaleId equals sale.Id where refund.EmployeeShiftId == shift.Id select new { refund.Amount, sale.PaymentMethod }).ToListAsync(ct);
        var gaming = shift.GamingSalesSnapshot ?? await GamingTotal(shift, clock.GetUtcNow().UtcDateTime, ct);
        decimal Net(PaymentMethod method) => sales.Where(x => x.PaymentMethod == method).Sum(x => x.CapturedTotal) - refunds.Where(x => x.PaymentMethod == method).Sum(x => x.Amount);
        var cash = Net(PaymentMethod.Cash); var expected = shift.OpeningCash + cash;
        return new ShiftSummary(shift, gaming, sales.Sum(x => x.CapturedTotal), refunds.Sum(x => x.Amount), cash, Net(PaymentMethod.Card), Net(PaymentMethod.Wallet) + gaming, expected, shift.ClosingCash - expected);
    }

    private async Task<decimal> GamingTotal(EmployeeShift shift, DateTime until, CancellationToken ct) => -(await data.Query<WalletTransaction>().Where(x => x.EmployeeId == shift.EmployeeId && x.Type == WalletTransactionType.GamingCharge && x.CreatedAtUtc >= shift.OpenedAtUtc && x.CreatedAtUtc < until).SumAsync(x => (decimal?)x.Amount, ct) ?? 0);
    private async Task<PosOperation?> FindOperation(Guid id, string kind, string fingerprint, CancellationToken ct)
    {
        var operation = await data.Query<PosOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (operation != null && (operation.Kind != kind || operation.Fingerprint != fingerprint)) throw new ClubException("IDEMPOTENCY_CONFLICT");
        if (operation == null && (await data.Query<WalletTransaction>().AnyAsync(x => x.OperationId == id, ct) ||
            await data.Query<GamingSession>().AnyAsync(x => x.Id == id, ct) ||
            await data.Query<SessionOperation>().AnyAsync(x => x.Id == id, ct))) throw new ClubException("IDEMPOTENCY_CONFLICT");
        return operation;
    }
    private void Audit(string type, Guid id, Guid employee) => data.Add(new SecurityAuditEvent(Guid.NewGuid(), type, null, clock.GetUtcNow().UtcDateTime, null, $"ShiftId={id:D};EmployeeId={employee:D}"));
}
