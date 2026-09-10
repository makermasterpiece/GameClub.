using GameClub.Domain.Commands;
using GameClub.Domain.Stations;
using GameClub.Domain.Security;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using GameClub.Server.Services.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameClub.Server.Tests.Commands;

public sealed class AgentCommandServiceTests
{
    [Fact]
    public async Task CreateCommand_CreatesPendingCommand()
    {
        await using var context = CreateContext();
        var station = AddStation(context);
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(context, new FakeTransport(), time);

        var command = await service.CreateCommandAsync(
            station.Id,
            AgentCommandType.Ping,
            null,
            CancellationToken.None);

        Assert.NotNull(command);
        Assert.Equal(AgentCommandStatus.Pending, command.Status);
        Assert.Equal(time.GetUtcNow().UtcDateTime, command.CreatedAtUtc);
        Assert.Equal(
            time.GetUtcNow().UtcDateTime + AgentCommandService.CommandLifetime,
            command.ExpiresAtUtc);
    }

    [Fact]
    public async Task CreateCommand_UnknownStation_ReturnsNull()
    {
        await using var context = CreateContext();
        var service = CreateService(context, new FakeTransport(), new TestTimeProvider(DateTimeOffset.UtcNow));

        var command = await service.CreateCommandAsync(
            Guid.NewGuid(),
            AgentCommandType.Ping,
            null,
            CancellationToken.None);

        Assert.Null(command);
        Assert.Empty(context.AgentCommands);
    }

    [Fact]
    public async Task Acknowledge_FromOwningStation_SetsAcknowledged()
    {
        await using var context = CreateContext();
        var station = AddStation(context);
        var transport = new FakeTransport { Connected = true };
        var service = CreateService(context, transport, new TestTimeProvider(DateTimeOffset.UtcNow));
        var command = await CreateAndDispatch(service, station.Id);

        var result = await service.AcknowledgeAsync(command.Id, station.Id, CancellationToken.None);

        Assert.Equal(CommandOperationResult.Success, result);
        Assert.Equal(AgentCommandStatus.Acknowledged, command.Status);
        Assert.NotNull(command.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Acknowledge_FromDifferentStation_IsRejected()
    {
        await using var context = CreateContext();
        var owner = AddStation(context, "PC-01", "MACHINE-01");
        var other = AddStation(context, "PC-02", "MACHINE-02");
        var service = CreateService(
            context,
            new FakeTransport { Connected = true },
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var command = await CreateAndDispatch(service, owner.Id);

        var result = await service.AcknowledgeAsync(command.Id, other.Id, CancellationToken.None);

        Assert.Equal(CommandOperationResult.StationMismatch, result);
        Assert.Equal(AgentCommandStatus.Sent, command.Status);
        Assert.Null(command.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Complete_AfterAcknowledgement_SetsCompleted()
    {
        await using var context = CreateContext();
        var station = AddStation(context);
        var service = CreateService(
            context,
            new FakeTransport { Connected = true },
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var command = await CreateAndDispatch(service, station.Id);
        await service.AcknowledgeAsync(command.Id, station.Id, CancellationToken.None);

        var result = await service.CompleteAsync(command.Id, station.Id, CancellationToken.None);

        Assert.Equal(CommandOperationResult.Success, result);
        Assert.Equal(AgentCommandStatus.Completed, command.Status);
        Assert.NotNull(command.CompletedAtUtc);
    }

    [Fact]
    public async Task Fail_StoresSanitizedBoundedError()
    {
        await using var context = CreateContext();
        var station = AddStation(context);
        var service = CreateService(
            context,
            new FakeTransport { Connected = true },
            new TestTimeProvider(DateTimeOffset.UtcNow));
        var command = await CreateAndDispatch(service, station.Id);
        await service.AcknowledgeAsync(command.Id, station.Id, CancellationToken.None);

        var result = await service.FailAsync(
            command.Id,
            station.Id,
            new string('x', AgentCommandService.MaximumErrorLength + 100),
            CancellationToken.None);

        Assert.Equal(CommandOperationResult.Success, result);
        Assert.Equal(AgentCommandStatus.Failed, command.Status);
        Assert.NotNull(command.FailedAtUtc);
        Assert.Equal(AgentCommandService.MaximumErrorLength, command.ErrorMessage?.Length);
    }

    [Fact]
    public async Task Dispatch_AfterTtl_SetsExpiredAndDoesNotSend()
    {
        await using var context = CreateContext();
        var station = AddStation(context);
        var transport = new FakeTransport { Connected = true };
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
        var service = CreateService(context, transport, time);
        var command = await service.CreateCommandAsync(
            station.Id,
            AgentCommandType.Ping,
            null,
            CancellationToken.None);
        Assert.NotNull(command);
        time.Advance(TimeSpan.FromMinutes(6));

        command = await service.DispatchCommandAsync(command.Id, CancellationToken.None);

        Assert.NotNull(command);
        Assert.Equal(AgentCommandStatus.Expired, command.Status);
        Assert.Empty(transport.SentCommands);
    }

    [Fact]
    public async Task PendingCommand_ForOfflineStation_IsDeliveredAfterReconnect()
    {
        await using var context = CreateContext();
        var station = AddStation(context);
        station.MarkOffline(DateTime.UtcNow.AddMinutes(1), TimeSpan.FromSeconds(30));
        await context.SaveChangesAsync();
        var transport = new FakeTransport { Connected = false };
        var service = CreateService(context, transport, new TestTimeProvider(DateTimeOffset.UtcNow));
        var command = await service.CreateCommandAsync(
            station.Id,
            AgentCommandType.Ping,
            null,
            CancellationToken.None);
        Assert.NotNull(command);

        command = await service.DispatchCommandAsync(command.Id, CancellationToken.None);

        Assert.NotNull(command);
        Assert.Equal(AgentCommandStatus.Pending, command.Status);
        Assert.Null(command.SentAtUtc);
        Assert.Empty(transport.SentCommands);

        station.UpdateFromAgent(
            station.Name,
            station.MachineName,
            station.IpAddress,
            station.AgentVersion,
            DateTime.UtcNow.AddMinutes(2));
        await context.SaveChangesAsync();
        transport.Connected = true;

        await service.DispatchPendingCommandsAsync(station.Id, CancellationToken.None);
        await context.Entry(command).ReloadAsync();

        Assert.Equal(AgentCommandStatus.Sent, command.Status);
        Assert.NotNull(command.SentAtUtc);
        Assert.Single(transport.SentCommands);
    }

    private static GameClubDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GameClubDbContext(options);
    }

    private static Station AddStation(
        GameClubDbContext context,
        string name = "PC-01",
        string machineName = "MACHINE-01")
    {
        var station = new Station(
            Guid.NewGuid(),
            name,
            machineName,
            "127.0.0.1",
            "0.1.0",
            DateTime.UtcNow);
        context.Stations.Add(station);
        context.StationCredentials.Add(new StationCredential(
            Guid.NewGuid(),
            station.Id,
            "hash",
            "protected",
            DateTime.UtcNow));
        context.SaveChanges();
        return station;
    }

    private static AgentCommandService CreateService(
        GameClubDbContext context,
        FakeTransport transport,
        TimeProvider timeProvider) =>
        new(context, transport, new FakeSigner(), timeProvider, NullLogger<AgentCommandService>.Instance);

    private static async Task<AgentCommand> CreateAndDispatch(
        AgentCommandService service,
        Guid stationId)
    {
        var command = await service.CreateCommandAsync(
            stationId,
            AgentCommandType.Ping,
            null,
            CancellationToken.None);
        Assert.NotNull(command);

        command = await service.DispatchCommandAsync(command.Id, CancellationToken.None);
        Assert.NotNull(command);
        Assert.Equal(AgentCommandStatus.Sent, command.Status);
        return command;
    }

    private sealed class FakeTransport : IStationCommandTransport
    {
        public bool Connected { get; set; }

        public List<SignedAgentCommandEnvelope> SentCommands { get; } = [];

        public bool IsConnected(Guid stationId) => Connected;

        public Task SendAsync(
            Guid stationId,
            SignedAgentCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            SentCommands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSigner : IServerCommandSigner
    {
        public string PublicKeyBase64 => string.Empty;

        public SignedAgentCommandEnvelope CreateSignedEnvelope(AgentCommand command) =>
            new(
                command.Id,
                command.StationId,
                command.Type.ToString(),
                command.PayloadJson,
                command.CreatedAtUtc,
                command.ExpiresAtUtc,
                "nonce",
                "signature",
                "ECDSA_P256_SHA256");
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
