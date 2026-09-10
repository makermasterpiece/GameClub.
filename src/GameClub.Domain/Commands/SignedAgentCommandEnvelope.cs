namespace GameClub.Domain.Commands;

public sealed record SignedAgentCommandEnvelope(
    Guid CommandId,
    Guid StationId,
    string Type,
    string? PayloadJson,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string Nonce,
    string Signature,
    string SignatureAlgorithm);
