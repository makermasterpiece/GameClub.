using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GameClub.Agent.Configuration;
using GameClub.Agent.Models;
using GameClub.Agent.Security;
using GameClub.Domain.Security;
using GameClub.Contracts.Gaming;
using GameClub.Contracts.Games;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Services;

public sealed class StationApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public StationApiClient(
        HttpClient httpClient,
        IOptions<ServerOptions> serverOptions,
        IOptions<SecurityOptions> securityOptions,
        IHostEnvironment environment,
        TimeProvider timeProvider)
    {
        var baseAddress = ServerEndpointPolicy.Validate(
            serverOptions.Value,
            securityOptions.Value,
            environment);
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _timeProvider = timeProvider;
    }

    public async Task<EnrollStationResponse> EnrollAsync(
        EnrollStationRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/stations/enroll",
            request,
            SerializerOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnrollStationResponse>(
                   SerializerOptions,
                   cancellationToken)
               ?? throw new InvalidOperationException("Server returned an empty enrollment response.");
    }

    public async Task<HeartbeatResult> SendHeartbeatAsync(
        StationCredentialData credential,
        HeartbeatRequest heartbeat,
        CancellationToken cancellationToken)
    {
        var path = $"/api/stations/{credential.StationId:D}/heartbeat";
        var body = JsonSerializer.SerializeToUtf8Bytes(heartbeat, SerializerOptions);
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post,
            path,
            body,
            credential,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            return HeartbeatResult.CredentialRejected;
        }

        response.EnsureSuccessStatusCode();
        return HeartbeatResult.Success;
    }

    public async Task<AgentSessionTokenResponse> GetSessionTokenAsync(
        StationCredentialData credential,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/agent/session-token",
            [],
            credential,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentSessionTokenResponse>(
                   SerializerOptions,
                   cancellationToken)
               ?? throw new InvalidOperationException("Server returned an empty session token response.");
    }

    public async Task<PlayerApiOperationResult> LoginPlayerAsync(
        StationCredentialData credential,
        PlayerLoginApiRequest request,
        CancellationToken cancellationToken)
    {
        const string path = "/api/agent/player/login";
        var body = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post,
            path,
            body,
            credential,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var session = await response.Content.ReadFromJsonAsync<PlayerSessionApiResponse>(
                SerializerOptions,
                cancellationToken);
            return session is null
                ? new PlayerApiOperationResult(false, null, "SERVER_ERROR")
                : new PlayerApiOperationResult(true, session, null);
        }

        return new PlayerApiOperationResult(
            false,
            null,
            await ReadErrorCodeAsync(response, cancellationToken));
    }

    public async Task<PlayerApiOperationResult> GetCurrentPlayerSessionAsync(
        StationCredentialData credential,
        CancellationToken cancellationToken)
    {
        const string path = "/api/agent/player/session/current";
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Get,
            path,
            [],
            credential,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new PlayerApiOperationResult(true, null, null);
        }

        if (response.IsSuccessStatusCode)
        {
            var session = await response.Content.ReadFromJsonAsync<PlayerSessionApiResponse>(
                SerializerOptions,
                cancellationToken);
            return session is null
                ? new PlayerApiOperationResult(false, null, "SERVER_ERROR")
                : new PlayerApiOperationResult(true, session, null);
        }

        return new PlayerApiOperationResult(
            false,
            null,
            await ReadErrorCodeAsync(response, cancellationToken));
    }

    public async Task<PlayerApiOperationResult> LogoutPlayerAsync(
        StationCredentialData credential,
        Guid expectedSessionId,
        CancellationToken cancellationToken)
    {
        if (expectedSessionId == Guid.Empty)
            throw new ArgumentException("An expected player auth session is required.", nameof(expectedSessionId));
        const string path = "/api/agent/player/logout";
        var body = JsonSerializer.SerializeToUtf8Bytes(new PlayerLogoutApiRequest(expectedSessionId), SerializerOptions);
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post,
            path,
            body,
            credential,
            cancellationToken);
        return response.IsSuccessStatusCode
            ? new PlayerApiOperationResult(true, null, null)
            : new PlayerApiOperationResult(
                false,
                null,
                await ReadErrorCodeAsync(response, cancellationToken));
    }

    public async Task<StationGamingState> GetCurrentGamingSessionAsync(
        StationCredentialData credential,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/agent/gaming/session/current",
            [],
            credential,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<StationGamingState>(
            SerializerOptions, cancellationToken)
            ?? throw new HttpRequestException("Server returned an empty gaming state.");
        if (state.Session is { } session &&
            (session.Id == Guid.Empty || session.UserId == Guid.Empty ||
             session.StationId != credential.StationId || session.ElapsedSeconds < 0 ||
             session.RemainingSeconds < 0))
        {
            throw new HttpRequestException("Server returned an invalid gaming state.");
        }

        return state;
    }

    public async Task ReportGameInventoryAsync(
        StationCredentialData credential, StationGameInventoryRequest request, CancellationToken ct)
    {
        using var response = await SendAuthenticatedAsync(HttpMethod.Post, "/api/agent/games/inventory",
            JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions), credential, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<GameAuthorizationResponse> AuthorizeGameAsync(
        StationCredentialData credential, GameAuthorizationRequest request, CancellationToken ct)
    {
        using var response = await SendAuthenticatedAsync(HttpMethod.Post, "/api/agent/games/authorize",
            JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions), credential, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GameAuthorizationResponse>(SerializerOptions, ct)
            ?? throw new HttpRequestException("Server returned an empty game authorization.");
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpMethod method,
        string path,
        byte[] body,
        StationCredentialData credential,
        CancellationToken cancellationToken)
    {
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var secret = SecurityEncoding.FromBase64Url(credential.StationSecret);
        string signature;
        try
        {
            signature = HmacRequestAuthentication.Sign(
                secret,
                method.Method,
                path,
                timestamp,
                nonce,
                body);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        using var request = new HttpRequestMessage(method, path);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        request.Headers.TryAddWithoutValidation(
            HmacRequestAuthentication.StationIdHeader,
            credential.StationId.ToString("D"));
        request.Headers.TryAddWithoutValidation(HmacRequestAuthentication.TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(HmacRequestAuthentication.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(HmacRequestAuthentication.SignatureHeader, signature);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<PlayerApiErrorResponse>(
                SerializerOptions,
                cancellationToken);
            return string.IsNullOrWhiteSpace(error?.Code) ? "SERVER_ERROR" : error.Code;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "AGENT_AUTHENTICATION_FAILED"
                : "SERVER_ERROR";
        }
    }
}

public enum HeartbeatResult
{
    Success,
    CredentialRejected
}
