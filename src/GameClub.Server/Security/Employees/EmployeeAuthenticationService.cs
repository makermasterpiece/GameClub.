using System.Security.Cryptography;
using GameClub.Application.Abstractions;
using GameClub.Domain.Employees;
using GameClub.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Security.Employees;

public sealed record EmployeeLoginResult(EmployeeLoginResponse? Response, string? ErrorCode);

public sealed class EmployeeAuthenticationService(IClubData data, IPasswordHasher<Employee> hasher,
    EmployeeTokenService tokens, EmployeeLoginLimiter limiter, TimeProvider clock)
{
    private static readonly Lazy<Employee> UnknownEmployee = new(CreateUnknownEmployee);

    public async Task<EmployeeLoginResult> LoginAsync(string username, string password, string sourceIp, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!limiter.TryBegin(sourceIp, now)) return new EmployeeLoginResult(null, "RATE_LIMITED");
        var succeeded = false;
        try
        {
            var employee = await data.AtomicAsync(async token =>
            {
                var validInput = !string.IsNullOrWhiteSpace(username) &&
                    username.Trim().Length is >= Employee.MinimumUsernameLength and <= Employee.MaximumUsernameLength &&
                    password is { Length: > 0 and <= EmployeeManagementService.MaximumPasswordLength };
                var normalized = validInput ? Employee.NormalizeUsername(username) : string.Empty;
                var candidate = validInput
                    ? await data.Query<Employee>().SingleOrDefaultAsync(e => e.NormalizedUsername == normalized, token)
                    : null;
                var target = candidate ?? UnknownEmployee.Value;
                var verified = validInput
                    ? hasher.VerifyHashedPassword(target, target.PasswordHash, password)
                    : PasswordVerificationResult.Failed;
                if (candidate is null || !candidate.IsActive || verified == PasswordVerificationResult.Failed)
                {
                    data.Add(new SecurityAuditEvent(Guid.NewGuid(), "EmployeeLoginFailed", null, now, sourceIp,
                        candidate is null ? "Result=INVALID_CREDENTIALS" : $"EmployeeId={candidate.Id:D};Result=INVALID_CREDENTIALS"));
                    return null;
                }

                if (verified == PasswordVerificationResult.SuccessRehashNeeded)
                    candidate.SetPasswordHash(hasher.HashPassword(candidate, password));
                candidate.RecordLogin(now);
                data.Add(new SecurityAuditEvent(Guid.NewGuid(), "EmployeeLoginSucceeded", null, now, sourceIp,
                    $"EmployeeId={candidate.Id:D};Result=Success"));
                return candidate;
            }, ct);
            if (employee is null) return new EmployeeLoginResult(null, "INVALID_CREDENTIALS");
            var response = tokens.Create(employee);
            succeeded = true;
            return new EmployeeLoginResult(response, null);
        }
        finally { limiter.Complete(sourceIp, succeeded); }
    }

    public Task LogoutAsync(Guid employeeId, string? sourceIp, CancellationToken ct) => data.AtomicAsync(async token =>
    {
        var employee = await data.Query<Employee>().SingleOrDefaultAsync(e => e.Id == employeeId, token)
            ?? throw new ClubException("EMPLOYEE_NOT_FOUND");
        employee.RevokeSessions();
        data.Add(new SecurityAuditEvent(Guid.NewGuid(), "EmployeeLogout", null, clock.GetUtcNow().UtcDateTime, sourceIp,
            $"EmployeeId={employee.Id:D};AllSessionsRevoked=True"));
        return true;
    }, ct);

    private static Employee CreateUnknownEmployee()
    {
        var employee = new Employee(Guid.NewGuid(), "unknown-employee", EmployeeRole.Operator, DateTime.UnixEpoch);
        employee.SetPasswordHash(new PasswordHasher<Employee>().HashPassword(employee,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        return employee;
    }
}
