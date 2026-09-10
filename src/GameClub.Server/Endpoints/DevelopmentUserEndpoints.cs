using GameClub.Server.Contracts.Users;
using GameClub.Server.Services.Players;
using GameClub.Server.Security.Employees;
using GameClub.Server.Security;
using GameClub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Endpoints;

public static class DevelopmentUserEndpoints
{
    public static RouteHandlerBuilder MapDevelopmentUserEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(
                "/api/dev/users",
                async Task<IResult> (
                    CreateDevelopmentUserRequest request,
                    IUserProvisioningService users,
                    GameClubDbContext db,
                    ISecurityAuditService audit,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    var result = await users.CreateAsync(
                        request.Username,
                        request.Password,
                        request.DisplayName,
                        request.Email,
                        request.Phone,
                        cancellationToken);
                    if (!result.Succeeded || result.User is null)
                    {
                        if (result.Error == CreateUserError.DuplicateUsername)
                        {
                            return Results.Conflict(new { code = "USERNAME_ALREADY_EXISTS" });
                        }

                        var field = result.Error switch
                        {
                            CreateUserError.InvalidUsername => "username",
                            CreateUserError.InvalidPassword => "password",
                            CreateUserError.InvalidDisplayName => "displayName",
                            CreateUserError.InvalidContact => "contact",
                            _ => "request"
                        };
                        return Results.ValidationProblem(new Dictionary<string, string[]>
                        {
                            [field] = ["The supplied value is invalid."]
                        });
                    }

                    var user = result.User;
                    await audit.WriteAsync("PlayerAccountCreated", null, context.Connection.RemoteIpAddress?.ToString(),
                        $"EmployeeId={context.User.FindFirst("employee_id")!.Value};Endpoint=Development", cancellationToken, user.Id);
                    await transaction.CommitAsync(cancellationToken);
                    return Results.Json(
                        new DevelopmentUserResponse(
                            user.Id,
                            user.Username,
                            user.DisplayName,
                            user.Email,
                            user.Phone,
                            user.Status,
                            user.CreatedAtUtc),
                        statusCode: StatusCodes.Status201Created);
                })
            .WithName("CreateDevelopmentUser")
            .RequireAuthorization(EmployeePermissions.Operate)
            .WithTags("Development")
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024))
            .Accepts<CreateDevelopmentUserRequest>("application/json")
            .Produces<DevelopmentUserResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status409Conflict)
            .ProducesValidationProblem();
}
