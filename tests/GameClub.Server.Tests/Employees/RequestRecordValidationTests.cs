using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using GameClub.Domain.Employees;
using GameClub.Domain.Users;
using GameClub.Server.Contracts.Commands;
using GameClub.Server.Contracts.Security;
using GameClub.Server.Contracts.Stations;
using GameClub.Server.Contracts.Users;
using GameClub.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlayerLoginRequest = GameClub.Server.Contracts.Players.PlayerLoginRequest;

namespace GameClub.Server.Tests.Employees;

public sealed class RequestRecordValidationTests
{
    [Fact]
    public void MvcObjectValidator_AcceptsValidPositionalRecordsWithoutMetadataException()
    {
        object[] requests =
        [
            new EmployeeLoginRequest("operator", "TestPassword!234"),
            new CreateEmployeeRequest("operator", "TestPassword!234", EmployeeRole.Operator),
            new ChangeEmployeeAccessRequest(EmployeeRole.Operator, true),
            new ChangeEmployeePasswordRequest("TestPassword!234"),
            new PlayerStatusRequest(UserStatus.Active),
            new CreateDevelopmentUserRequest("player", "TestPassword!234", "Player", "player@example.test"),
            new RegisterStationRequest("PC-01", "DESKTOP-01", "0.1.0"),
            new EnrollStationRequest("enrollment-test-value", "PC-01", "DESKTOP-01", "0.1.0"),
            new CreateEnrollmentTokenRequest("Test station"),
            new PlayerLoginRequest("player", "TestPassword!234"),
            new GameClub.Server.Contracts.Players.PlayerLogoutRequest(Guid.NewGuid()),
            new ExtendGamingSessionRequest(Guid.NewGuid(), 15),
            new TransferGamingSessionRequest(Guid.NewGuid(), Guid.NewGuid()),
            new CreateAgentCommandRequest("Ping", null),
            new HeartbeatRequest("DESKTOP-01", "0.1.0")
        ];

        foreach (var request in requests)
        {
            var actionContext = Validate(request);
            Assert.True(actionContext.ModelState.IsValid, $"MVC rejected valid {request.GetType().Name}.");
        }
    }

    [Fact]
    public void MvcObjectValidator_EnforcesConstructorParameterAnnotations()
    {
        (object Request, string Field)[] cases =
        [
            (new EmployeeLoginRequest("", "TestPassword!234"), "Username"),
            (new CreateEmployeeRequest("operator", "TestPassword!234", null), "Role"),
            (new ChangeEmployeeAccessRequest(EmployeeRole.Operator, null), "IsActive"),
            (new ChangeEmployeePasswordRequest("short"), "Password"),
            (new PlayerStatusRequest((UserStatus)999), "Status"),
            (new CreateDevelopmentUserRequest("player", "TestPassword!234", "Player", "invalid-email"), "Email"),
            (new RegisterStationRequest("PC-01", new string('x', 256), "0.1.0"), "MachineName"),
            (new EnrollStationRequest("", "PC-01", "DESKTOP-01", "0.1.0"), "EnrollmentToken"),
            (new CreateEnrollmentTokenRequest(new string('x', 501)), "Description"),
            (new PlayerLoginRequest("player", ""), "Password"),
            (new GameClub.Server.Contracts.Players.PlayerLogoutRequest(null), "ExpectedSessionId"),
            (new ExtendGamingSessionRequest(null, 15), "OperationId"),
            (new ExtendGamingSessionRequest(Guid.NewGuid(), 0), "Minutes"),
            (new ExtendGamingSessionRequest(Guid.NewGuid(), 10081), "Minutes"),
            (new TransferGamingSessionRequest(null, Guid.NewGuid()), "OperationId"),
            (new TransferGamingSessionRequest(Guid.NewGuid(), null), "DestinationStationId"),
            (new CreateAgentCommandRequest("", null), "Type"),
            (new HeartbeatRequest("DESKTOP-01", new string('x', 51)), "AgentVersion")
        ];

        foreach (var (request, field) in cases)
        {
            var actionContext = Validate(request);
            Assert.False(actionContext.ModelState.IsValid);
            Assert.Contains(actionContext.ModelState, item => item.Key == field && item.Value is { Errors.Count: > 0 });
        }
    }

    [Fact]
    public void MissingPlayerStatus_CannotDefaultToActiveAndUnbanAPlayer()
    {
        var request = JsonSerializer.Deserialize<PlayerStatusRequest>("{}")!;
        Assert.Null(request.Status);
        var result = Validate(request);
        Assert.False(result.ModelState.IsValid);
        Assert.Contains(result.ModelState, item => item.Key == "Status" && item.Value is { Errors.Count: > 0 });
    }

    [Fact]
    public void AllRequestRecordBoundProperties_KeepValidationOnConstructorParameters()
    {
        var records = typeof(EmployeeLoginRequest).Assembly.GetTypes().Where(type =>
            type.Name.EndsWith("Request", StringComparison.Ordinal) &&
            type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) is not null);
        foreach (var record in records)
        {
            var constructorParameterNames = record.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                .Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var property in record.GetProperties().Where(property => constructorParameterNames.Contains(property.Name)))
                Assert.Empty(property.GetCustomAttributes<ValidationAttribute>());
        }
    }

    private static ActionContext Validate(object request)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());
        provider.GetRequiredService<IObjectModelValidator>().Validate(context, null, string.Empty, request);
        return context;
    }
}
