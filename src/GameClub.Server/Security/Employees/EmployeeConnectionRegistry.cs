using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using GameClub.Domain.Employees;

namespace GameClub.Server.Security.Employees;

public sealed record EmployeeConnectionIdentity(Guid EmployeeId, Guid SecurityStamp, string Role, DateTime ExpiresAtUtc)
{
    public ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity([
        new Claim(EmployeeAuthenticationDefaults.EmployeeIdClaim, EmployeeId.ToString("D")),
        new Claim(EmployeeAuthenticationDefaults.StampClaim, SecurityStamp.ToString("D")),
        new Claim("role", Role)], EmployeeAuthenticationDefaults.Scheme));
}

public sealed record EmployeeConnectionRegistration(Guid RegistrationId, string ConnectionId,
    EmployeeConnectionIdentity Identity, Action Abort);

/// <summary>Stores only immutable identity fields and a connection-lifetime abort callback, never a JWT or scoped service.</summary>
public sealed class EmployeeConnectionRegistry
{
    private readonly ConcurrentDictionary<string, EmployeeConnectionRegistration> _connections = new(StringComparer.Ordinal);

    public bool TryRegister(string connectionId, ClaimsPrincipal? principal, Action abort)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || principal?.Identity?.IsAuthenticated != true ||
            principal.FindAll(EmployeeAuthenticationDefaults.EmployeeIdClaim).Count() != 1 ||
            principal.FindAll(EmployeeAuthenticationDefaults.StampClaim).Count() != 1 ||
            principal.FindAll("role").Count() != 1 || principal.FindAll("exp").Count() != 1 ||
            !Guid.TryParse(principal.FindFirst(EmployeeAuthenticationDefaults.EmployeeIdClaim)?.Value, out var employeeId) ||
            employeeId == Guid.Empty ||
            !Guid.TryParse(principal.FindFirst(EmployeeAuthenticationDefaults.StampClaim)?.Value, out var stamp) || stamp == Guid.Empty ||
            !long.TryParse(principal.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var expiration)) return false;
        var role = principal.FindFirst("role")!.Value;
        if (!Enum.TryParse<EmployeeRole>(role, out var parsedRole) || !Enum.IsDefined(parsedRole) || parsedRole.ToString() != role)
            return false;
        DateTime expiresAtUtc;
        try { expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiration).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return false; }
        return _connections.TryAdd(connectionId, new EmployeeConnectionRegistration(Guid.NewGuid(), connectionId,
            new EmployeeConnectionIdentity(employeeId, stamp, role, expiresAtUtc), abort));
    }

    public IReadOnlyCollection<EmployeeConnectionRegistration> Snapshot() => _connections.Values.ToArray();

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public bool TryAbort(EmployeeConnectionRegistration registration)
    {
        // A disconnected connection id may have been reused after this snapshot was taken.
        if (!((ICollection<KeyValuePair<string, EmployeeConnectionRegistration>>)_connections)
                .Remove(new KeyValuePair<string, EmployeeConnectionRegistration>(registration.ConnectionId, registration))) return false;
        try { registration.Abort(); }
        catch (ObjectDisposedException) { }
        return true;
    }
}
