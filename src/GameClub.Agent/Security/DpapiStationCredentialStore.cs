using System.Security.Cryptography;
using System.Text.Json;
using GameClub.Agent.Configuration;
using GameClub.Agent.Models;
using Microsoft.Extensions.Options;

namespace GameClub.Agent.Security;

public sealed class DpapiStationCredentialStore(
    IOptions<SecurityOptions> options,
    ILogger<DpapiStationCredentialStore> logger) : IStationCredentialStore
{
    private static readonly byte[] Entropy = "GameClub.Agent.Credential.v1"u8.ToArray();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path = ResolvePath(options.Value.CredentialFilePath);

    public async Task<StationCredentialData?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The production credential store requires Windows DPAPI.");
        }
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
            try
            {
                var credential = JsonSerializer.Deserialize<StationCredentialData>(
                    plaintext,
                    SerializerOptions);
                return credential is null || credential.StationId == Guid.Empty ||
                       string.IsNullOrWhiteSpace(credential.StationSecret) ||
                       string.IsNullOrWhiteSpace(credential.ServerPublicKey)
                    ? null
                    : credential;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException exception)
        {
            logger.LogError(exception, "Could not decrypt the local station credential");
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "The local station credential is invalid");
            return null;
        }
    }

    public async Task SaveAsync(
        StationCredentialData credential,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The production credential store requires Windows DPAPI.");
        }
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Credential path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, SerializerOptions);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.LocalMachine);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        logger.LogInformation("Stored protected credential for station {StationId}", credential.StationId);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    private static string ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "GameClub", "Agent", "credentials.dat");
    }
}
