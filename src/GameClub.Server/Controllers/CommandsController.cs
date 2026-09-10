using GameClub.Infrastructure.Persistence;
using GameClub.Server.Contracts.Commands;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using GameClub.Server.Security.Employees;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/commands")]
public sealed class CommandsController(GameClubDbContext dbContext) : ControllerBase
{
    [HttpGet("{commandId:guid}", Name = RouteName)]
    [Authorize(AuthenticationSchemes = EmployeeAuthenticationDefaults.Scheme, Policy = EmployeePermissions.Read)]
    [ProducesResponseType<AgentCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentCommandResponse>> GetById(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var command = await dbContext.AgentCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == commandId, cancellationToken);

        return command is null ? NotFound() : Ok(command.ToResponse());
    }

    public const string RouteName = "GetAgentCommand";
}
