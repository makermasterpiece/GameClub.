using System.Globalization;
using System.Security.Cryptography;
using GameClub.Domain.Security;
using GameClub.Domain.Stations;
using GameClub.Infrastructure.Persistence;
using GameClub.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameClub.Server.Tests.Security;

public sealed class StationHmacAuthenticatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidHmacAuthentication_Succeeds()
    {
        var fixture = CreateFixture();
        var request = CreateSignedRequest(fixture.StationId, fixture.Secret, Now);

        var result = await fixture.Authenticator.AuthenticateAsync(request, "127.0.0.1", default);

        Assert.True(result.Succeeded);
        Assert.Equal(fixture.StationId, result.StationId);
    }

    [Fact]
    public async Task InvalidHmac_IsRejected()
    {
        var fixture = CreateFixture();
        var request = CreateSignedRequest(fixture.StationId, fixture.Secret, Now);
        request.Headers[HmacRequestAuthentication.SignatureHeader] = "invalid";

        var result = await fixture.Authenticator.AuthenticateAsync(request, null, default);

        Assert.Equal(StationAuthenticationFailure.InvalidSignature, result.Failure);
    }

    [Fact]
    public async Task WrongStationId_IsRejected()
    {
        var fixture = CreateFixture();
        var request = CreateSignedRequest(Guid.NewGuid(), fixture.Secret, Now);

        var result = await fixture.Authenticator.AuthenticateAsync(request, null, default);

        Assert.Equal(StationAuthenticationFailure.CredentialNotFound, result.Failure);
    }

    [Fact]
    public async Task ExpiredTimestamp_IsRejected()
    {
        var fixture = CreateFixture();
        var request = CreateSignedRequest(fixture.StationId, fixture.Secret, Now.AddSeconds(-61));

        var result = await fixture.Authenticator.AuthenticateAsync(request, null, default);

        Assert.Equal(StationAuthenticationFailure.TimestampOutsideWindow, result.Failure);
    }

    [Fact]
    public async Task FutureTimestamp_IsRejected()
    {
        var fixture = CreateFixture();
        var request = CreateSignedRequest(fixture.StationId, fixture.Secret, Now.AddSeconds(61));

        var result = await fixture.Authenticator.AuthenticateAsync(request, null, default);

        Assert.Equal(StationAuthenticationFailure.TimestampOutsideWindow, result.Failure);
    }

    [Fact]
    public async Task ReusedNonce_IsRejected()
    {
        var fixture = CreateFixture();
        var nonce = SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var first = CreateSignedRequest(fixture.StationId, fixture.Secret, Now, nonce);
        var second = CreateSignedRequest(fixture.StationId, fixture.Secret, Now, nonce);

        Assert.True((await fixture.Authenticator.AuthenticateAsync(first, null, default)).Succeeded);
        var result = await fixture.Authenticator.AuthenticateAsync(second, null, default);

        Assert.Equal(StationAuthenticationFailure.ReplayDetected, result.Failure);
    }

    [Fact]
    public async Task RevokedCredential_IsRejected()
    {
        var fixture = CreateFixture(revoked: true);
        var request = CreateSignedRequest(fixture.StationId, fixture.Secret, Now);

        var result = await fixture.Authenticator.AuthenticateAsync(request, null, default);

        Assert.Equal(StationAuthenticationFailure.CredentialNotFound, result.Failure);
    }

    private static Fixture CreateFixture(bool revoked = false)
    {
        var options = new DbContextOptionsBuilder<GameClubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var context = new GameClubDbContext(options);
        var stationId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        context.Stations.Add(new Station(
            stationId,
            "PC-01",
            "MACHINE-01",
            null,
            "0.1.0",
            Now.UtcDateTime));
        var credential = new StationCredential(
            Guid.NewGuid(),
            stationId,
            SecurityEncoding.Sha256Hex(secret),
            "protected",
            Now.UtcDateTime);
        if (revoked)
        {
            credential.Revoke(Now.UtcDateTime);
        }

        context.StationCredentials.Add(credential);
        context.SaveChanges();
        var time = new FixedTimeProvider(Now);
        var authenticator = new StationHmacAuthenticator(
            context,
            new FakeSecretProtector(secret),
            new InMemoryReplayProtectionStore(time),
            new NullAuditService(),
            time,
            NullLogger<StationHmacAuthenticator>.Instance);
        return new Fixture(stationId, secret, authenticator);
    }

    private static HttpRequest CreateSignedRequest(
        Guid stationId,
        byte[] secret,
        DateTimeOffset timestamp,
        string? nonce = null)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = HttpMethods.Post;
        request.Path = $"/api/stations/{stationId:D}/heartbeat";
        var body = "{}"u8.ToArray();
        request.Body = new MemoryStream(body);
        var timestampValue = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        nonce ??= SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(16));
        request.Headers[HmacRequestAuthentication.StationIdHeader] = stationId.ToString("D");
        request.Headers[HmacRequestAuthentication.TimestampHeader] = timestampValue;
        request.Headers[HmacRequestAuthentication.NonceHeader] = nonce;
        request.Headers[HmacRequestAuthentication.SignatureHeader] = HmacRequestAuthentication.Sign(
            secret,
            request.Method,
            request.Path,
            timestampValue,
            nonce,
            body);
        return request;
    }

    private sealed record Fixture(
        Guid StationId,
        byte[] Secret,
        StationHmacAuthenticator Authenticator);

    private sealed class FakeSecretProtector(byte[] secret) : IStationSecretProtector
    {
        public string Protect(byte[] value) => "protected";
        public byte[] Unprotect(string protectedSecret) => secret.ToArray();
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
