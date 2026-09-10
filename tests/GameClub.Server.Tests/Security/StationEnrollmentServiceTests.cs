using GameClub.Domain.Commands;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GameClub.Server.Tests.Security;

public sealed class StationEnrollmentServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnrollmentToken_IsStoredOnlyAsHash()
    {
        await using var fixture = CreateFixture();

        var created = await fixture.Service.CreateTokenAsync("PC row 1", default);
        var stored = await fixture.Context.StationEnrollmentTokens.SingleAsync();

        Assert.NotEqual(created.Token, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
    }

    [Fact]
    public async Task ReusedEnrollmentToken_IsRejected()
    {
        await using var fixture = CreateFixture();
        var token = await fixture.Service.CreateTokenAsync(null, default);

        var first = await Enroll(fixture.Service, token.Token);
        var second = await Enroll(fixture.Service, token.Token);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task ExpiredEnrollmentToken_IsRejected()
    {
        await using var fixture = CreateFixture();
        var token = await fixture.Service.CreateTokenAsync(null, default);
        fixture.Time.Advance(TimeSpan.FromMinutes(16));

        var result = await Enroll(fixture.Service, token.Token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RevokedEnrollmentToken_IsRejected()
    {
        await using var fixture = CreateFixture();
        var token = await fixture.Service.CreateTokenAsync(null, default);
        var stored = await fixture.Context.StationEnrollmentTokens.SingleAsync();
        stored.Revoke();
        await fixture.Context.SaveChangesAsync();

        var result = await Enroll(fixture.Service, token.Token);

        Assert.False(result.Succeeded);
    }

    private static Task<StationEnrollmentResult> Enroll(
        IStationEnrollmentService service,
        string token) =>
        service.EnrollAsync(
            token,
            "PC-01",
            "MACHINE-01",
            "0.1.0",
            "127.0.0.1",
            default);

    private static Fixture CreateFixture()
    {
        var options = new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var context = new GameClubDbContext(options);
        var time = new MutableTimeProvider(Start);
        var service = new StationEnrollmentService(
            context,
            new FakeSecretProtector(),
            new FakeCommandSigner(),
            new NullAuditService(),
            time);
        return new Fixture(context, service, time);
    }

    private sealed class FakeSecretProtector : IStationSecretProtector
    {
        public string Protect(byte[] secret) => Convert.ToBase64String(secret);
        public byte[] Unprotect(string protectedSecret) => Convert.FromBase64String(protectedSecret);
    }

    private sealed class FakeCommandSigner : IServerCommandSigner
    {
        public string PublicKeyBase64 => "test-public-key";
        public SignedAgentCommandEnvelope CreateSignedEnvelope(AgentCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class NullAuditService : ISecurityAuditService
    {
        public Task WriteAsync(
            string eventType,
            Guid? stationId,
            string? sourceIp,
            string? details,
            CancellationToken cancellationToken,
            Guid? userId = null) => Task.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan duration) => value += duration;
    }

    private sealed class Fixture(
        GameClubDbContext context,
        StationEnrollmentService service,
        MutableTimeProvider time) : IAsyncDisposable
    {
        public GameClubDbContext Context { get; } = context;
        public StationEnrollmentService Service { get; } = service;
        public MutableTimeProvider Time { get; } = time;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
