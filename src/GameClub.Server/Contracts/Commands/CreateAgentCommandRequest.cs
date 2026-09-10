using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace GameClub.Server.Contracts.Commands;

public sealed record CreateAgentCommandRequest(
    [param: Required, MaxLength(50)] string Type,
    JsonElement? Payload);
