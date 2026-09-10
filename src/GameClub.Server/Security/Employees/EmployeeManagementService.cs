using GameClub.Application.Abstractions;
using GameClub.Domain.Employees;
using GameClub.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Security.Employees;

public sealed record EmployeeAdminResponse(Guid Id, string Username, string Role, bool IsActive,
    DateTime CreatedAtUtc, DateTime? LastLoginAtUtc);

public sealed class EmployeeManagementService(IClubData data, IPasswordHasher<Employee> hasher, TimeProvider clock)
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    public Task<EmployeeAdminResponse> CreateAsync(Guid actorId, string username, string password,
        EmployeeRole role, CancellationToken ct) => data.AtomicAsync(async token =>
    {
        await RequireAdministratorAsync(actorId, token);
        return await CreateCoreAsync(username, password, role, actorId, "EmployeeCreated", token);
    }, ct);

    public Task<EmployeeAdminResponse> BootstrapAsync(string username, string password, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            if (await data.Query<Employee>().AnyAsync(token)) throw new ClubException("BOOTSTRAP_ALREADY_COMPLETED");
            return await CreateCoreAsync(username, password, EmployeeRole.Administrator, null, "EmployeeBootstrapped", token);
        }, ct);

    public Task<EmployeeAdminResponse> ChangeAccessAsync(Guid actorId, Guid id, EmployeeRole role, bool isActive,
        CancellationToken ct) => data.AtomicAsync(async token =>
    {
        await RequireAdministratorAsync(actorId, token);
        var employee = await RequiredAsync(id, token);
        if (employee.IsActive && employee.Role == EmployeeRole.Administrator &&
            (!isActive || role != EmployeeRole.Administrator) &&
            !await data.Query<Employee>().AnyAsync(e => e.Id != id && e.IsActive && e.Role == EmployeeRole.Administrator, token))
            throw new ClubException("LAST_ADMINISTRATOR_REQUIRED");
        employee.ChangeAccess(role, isActive);
        Audit("EmployeeAccessChanged", actorId, id, $"Role={role};IsActive={isActive}");
        return ToResponse(employee);
    }, ct);

    public Task ChangePasswordAsync(Guid actorId, Guid id, string password, CancellationToken ct) =>
        data.AtomicAsync(async token =>
        {
            await RequireAdministratorAsync(actorId, token);
            ValidatePassword(password);
            var employee = await RequiredAsync(id, token);
            employee.SetPasswordHash(hasher.HashPassword(employee, password));
            Audit("EmployeePasswordChanged", actorId, id, "SessionsRevoked=True");
            return true;
        }, ct);

    private async Task<EmployeeAdminResponse> CreateCoreAsync(string username, string password, EmployeeRole role,
        Guid? actorId, string eventType, CancellationToken ct)
    {
        ValidatePassword(password);
        var employee = new Employee(Guid.NewGuid(), username, role, clock.GetUtcNow().UtcDateTime);
        if (await data.Query<Employee>().AnyAsync(e => e.NormalizedUsername == employee.NormalizedUsername, ct))
            throw new ClubException("EMPLOYEE_USERNAME_EXISTS");
        employee.SetPasswordHash(hasher.HashPassword(employee, password));
        data.Add(employee);
        Audit(eventType, actorId, employee.Id, $"Role={role}");
        return ToResponse(employee);
    }

    private async Task RequireAdministratorAsync(Guid actorId, CancellationToken ct)
    {
        if (!await data.Query<Employee>().AnyAsync(e => e.Id == actorId && e.IsActive && e.Role == EmployeeRole.Administrator, ct))
            throw new ClubException("EMPLOYEE_PERMISSION_DENIED");
    }

    private async Task<Employee> RequiredAsync(Guid id, CancellationToken ct) =>
        await data.Query<Employee>().SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new ClubException("EMPLOYEE_NOT_FOUND");

    private void Audit(string eventType, Guid? actorId, Guid targetId, string details) =>
        data.Add(new SecurityAuditEvent(Guid.NewGuid(), eventType, null, clock.GetUtcNow().UtcDateTime, null,
            $"ActorEmployeeId={actorId?.ToString("D") ?? "Bootstrap"};TargetEmployeeId={targetId:D};{details}"));

    public static EmployeeAdminResponse ToResponse(Employee employee) => new(employee.Id, employee.Username,
        employee.Role.ToString(), employee.IsActive, employee.CreatedAtUtc, employee.LastLoginAtUtc);

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password is not { Length: >= MinimumPasswordLength and <= MaximumPasswordLength })
            throw new ArgumentException("Employee password must contain 12 to 128 characters.", nameof(password));
    }
}
