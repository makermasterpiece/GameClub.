using System.ComponentModel.DataAnnotations;

namespace GameClub.Server.Contracts.Security;

public sealed record CreateEnrollmentTokenRequest([param: MaxLength(500)] string? Description);

public sealed record CreateEnrollmentTokenResponse(string Token, DateTime ExpiresAtUtc);

public sealed record EnrollStationRequest(
    [param: Required, MaxLength(256)] string EnrollmentToken,
    [param: Required, MaxLength(100)] string StationName,
    [param: Required, MaxLength(255)] string MachineName,
    [param: Required, MaxLength(50)] string AgentVersion);

public sealed record EnrollStationResponse(
    Guid StationId,
    string StationSecret,
    string ServerPublicKey,
    string SignatureAlgorithm);

public sealed record AgentSessionTokenResponse(string AccessToken, DateTime ExpiresAtUtc);
