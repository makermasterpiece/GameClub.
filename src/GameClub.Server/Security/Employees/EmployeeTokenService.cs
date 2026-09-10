using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GameClub.Domain.Employees;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GameClub.Server.Security.Employees;

public sealed record EmployeeIdentityResponse(Guid Id, string Username, string Role, string[] Permissions);
public sealed record EmployeeLoginResponse(string AccessToken, DateTime ExpiresAtUtc, EmployeeIdentityResponse Employee);

public sealed class EmployeeTokenService(IServerSigningKeyProvider keys, IOptions<SecurityOptions> security,
    EmployeeSecurityOptions employeeOptions, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    public EmployeeLoginResponse Create(Employee employee)
    {
        if (!employee.IsActive) throw new InvalidOperationException("Inactive employees cannot receive tokens.");
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now + Lifetime;
        var identity = Identity(employee);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, employee.Id.ToString("D")),
            new(EmployeeAuthenticationDefaults.EmployeeIdClaim, employee.Id.ToString("D")),
            new(EmployeeAuthenticationDefaults.StampClaim, employee.SecurityStamp.ToString("D")),
            new("role", employee.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D"))
        };
        claims.AddRange(identity.Permissions.Select(p => new Claim(EmployeeAuthenticationDefaults.PermissionClaim, p)));
        var token = new JwtSecurityToken(security.Value.JwtIssuer, EmployeeAuthenticationDefaults.Audience,
            claims, now.AddSeconds(-5), expires, keys.JwtSigningCredentials);
        return new EmployeeLoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, identity);
    }

    public EmployeeIdentityResponse Identity(Employee employee) => new(employee.Id, employee.Username,
        employee.Role.ToString(), EmployeePermissions.ForRole(employee.Role, employeeOptions.OperatorCanPower));

    public static TokenValidationParameters ValidationParameters(IServerSigningKeyProvider keys, SecurityOptions options) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = keys.JwtValidationKey,
        ValidateIssuer = true,
        ValidIssuer = options.JwtIssuer,
        ValidateAudience = true,
        ValidAudience = EmployeeAuthenticationDefaults.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        ClockSkew = TimeSpan.FromSeconds(15),
        NameClaimType = EmployeeAuthenticationDefaults.EmployeeIdClaim,
        RoleClaimType = "role"
    };
}

public sealed class EmployeePrincipalValidator(GameClubDbContext db, EmployeeSecurityOptions options)
{
    public async Task<bool> ValidateAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (principal?.Identity is not ClaimsIdentity identity ||
            principal.FindAll(EmployeeAuthenticationDefaults.EmployeeIdClaim).Count() != 1 ||
            principal.FindAll(EmployeeAuthenticationDefaults.StampClaim).Count() != 1 ||
            !Guid.TryParse(principal.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim)?.Value, out var id) ||
            !Guid.TryParse(principal.FindFirst(EmployeeAuthenticationDefaults.StampClaim)?.Value, out var stamp)) return false;
        var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null || !employee.IsActive || employee.SecurityStamp != stamp ||
            principal.FindFirst("role")?.Value != employee.Role.ToString()) return false;

        // Authorization comes from the current database role/configuration, never a stale token's permission list.
        foreach (var claim in identity.FindAll(EmployeeAuthenticationDefaults.PermissionClaim).ToArray()) identity.RemoveClaim(claim);
        foreach (var permission in EmployeePermissions.ForRole(employee.Role, options.OperatorCanPower))
            identity.AddClaim(new Claim(EmployeeAuthenticationDefaults.PermissionClaim, permission));
        return true;
    }
}
