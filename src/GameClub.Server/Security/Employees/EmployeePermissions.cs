using GameClub.Domain.Employees;

namespace GameClub.Server.Security.Employees;

public static class EmployeeAuthenticationDefaults
{
    public const string Scheme = "EmployeeJwt";
    public const string Audience = "gameclub-employee";
    public const string EmployeeIdClaim = "employee_id";
    public const string PermissionClaim = "permission";
    public const string StampClaim = "security_stamp";
}

public static class EmployeePermissions
{
    public const string Read = "ClubRead";
    public const string Operate = "ClubOperate";
    public const string Catalog = "ClubManageCatalog";
    public const string Money = "ClubManageMoney";
    public const string Employees = "ClubManageEmployees";
    public const string Security = "ClubManageSecurity";
    public const string Power = "ClubPower";
    public static readonly string[] All = [Read, Operate, Catalog, Money, Employees, Security, Power];

    public static string[] ForRole(EmployeeRole role, bool operatorCanPower = false) => role switch
    {
        EmployeeRole.Administrator => [.. All],
        EmployeeRole.Manager => [Read, Operate, Catalog, Money, Power],
        EmployeeRole.Operator => operatorCanPower ? [Read, Operate, Power] : [Read, Operate],
        _ => []
    };
}

public sealed class EmployeeSecurityOptions
{
    public bool OperatorCanPower { get; set; }
}
