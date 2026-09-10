using System.ComponentModel.DataAnnotations;

namespace GameClub.Agent.Models;

public sealed record EnrollStationRequest(
    string EnrollmentToken,
    string StationName,
    string MachineName,
    string AgentVersion);

public sealed record EnrollStationResponse(
    Guid StationId,
    string StationSecret,
    string ServerPublicKey,
    string SignatureAlgorithm);

public sealed record AgentSessionTokenResponse(string AccessToken, DateTime ExpiresAtUtc);

public sealed record StationCredentialData(
    Guid StationId,
    string StationSecret,
    string ServerPublicKey,
    string SignatureAlgorithm);
