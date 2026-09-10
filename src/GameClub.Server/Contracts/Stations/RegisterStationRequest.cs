using System.ComponentModel.DataAnnotations;

namespace GameClub.Server.Contracts.Stations;

public sealed record RegisterStationRequest(
    [param: Required, MaxLength(100)] string Name,
    [param: Required, MaxLength(255)] string MachineName,
    [param: Required, MaxLength(50)] string AgentVersion);
