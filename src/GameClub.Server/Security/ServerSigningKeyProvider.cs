using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameClub.Domain.Commands;
using GameClub.Domain.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GameClub.Server.Security;

public interface IServerCommandSigner
{
    SignedAgentCommandEnvelope CreateSignedEnvelope(AgentCommand command);
    string PublicKeyBase64 { get; }
}

public interface IServerSigningKeyProvider : IServerCommandSigner
{
    SigningCredentials JwtSigningCredentials { get; }
    SecurityKey JwtValidationKey { get; }
}

public sealed class ServerSigningKeyProvider : IServerSigningKeyProvider, IDisposable
{
    private readonly ECDsa _commandKey;
    private readonly ECDsa _jwtKey;
    private readonly ECDsa _jwtValidationKey;
    private readonly object _signingLock = new();

    public ServerSigningKeyProvider(
        IOptions<SecurityOptions> options,
        IHostEnvironment environment,
        ILogger<ServerSigningKeyProvider> logger)
    {
        _commandKey = LoadKey(options.Value, environment, logger);
        if (_commandKey.KeySize != 256)
        {
            throw new InvalidOperationException("Server command signing key must be ECDSA P-256.");
        }

        // PFX-backed Windows CNG keys need not permit plaintext export. Import a separate
        // ephemeral instance for JWT signing instead of extracting private key parameters.
        _jwtKey = !string.IsNullOrWhiteSpace(options.Value.CommandSigningCertificatePath)
            ? LoadKey(options.Value, environment, logger)
            : ECDsa.Create(_commandKey.ExportParameters(includePrivateParameters: true));
        _jwtValidationKey = ECDsa.Create(_jwtKey.ExportParameters(includePrivateParameters: false));
        JwtSigningCredentials = new SigningCredentials(
            new ECDsaSecurityKey(_jwtKey),
            SecurityAlgorithms.EcdsaSha256);
        JwtValidationKey = new ECDsaSecurityKey(_jwtValidationKey);
        PublicKeyBase64 = Convert.ToBase64String(_commandKey.ExportSubjectPublicKeyInfo());
    }

    public string PublicKeyBase64 { get; }
    public SigningCredentials JwtSigningCredentials { get; }
    public SecurityKey JwtValidationKey { get; }

    public SignedAgentCommandEnvelope CreateSignedEnvelope(AgentCommand command)
    {
        var unsigned = new SignedAgentCommandEnvelope(
            command.Id,
            command.StationId,
            command.Type.ToString(),
            command.PayloadJson,
            command.CreatedAtUtc,
            command.ExpiresAtUtc,
            SecurityEncoding.ToBase64Url(RandomNumberGenerator.GetBytes(16)),
            string.Empty,
            CommandEnvelopeCryptography.SignatureAlgorithm);
        string signature;
        lock (_signingLock)
        {
            signature = CommandEnvelopeCryptography.Sign(_commandKey, unsigned);
        }

        return unsigned with { Signature = signature };
    }

    public void Dispose()
    {
        _commandKey.Dispose();
        _jwtKey.Dispose();
        _jwtValidationKey.Dispose();
    }

    private static ECDsa LoadKey(
        SecurityOptions options,
        IHostEnvironment environment,
        ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.CommandSigningCertificatePath))
        {
            using var certificate = new X509Certificate2(
                Path.GetFullPath(options.CommandSigningCertificatePath),
                options.CommandSigningCertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
            // GetECDsaPrivateKey returns an independently owned key handle. The caller
            // disposes it; the certificate wrapper can be released without exporting it.
            var certificateKey = certificate.GetECDsaPrivateKey()
                ?? throw new InvalidOperationException("Configured certificate has no ECDSA private key.");
            logger.LogInformation("Loaded server ECDSA command signing key from an X.509 certificate");
            return certificateKey;
        }

        if (!string.IsNullOrWhiteSpace(options.CommandSigningPrivateKeyPath))
        {
            var path = Path.GetFullPath(options.CommandSigningPrivateKeyPath);
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(path));
            logger.LogInformation("Loaded server ECDSA command signing key from configured external file");
            return key;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "A command signing PEM key or X.509 certificate is required outside Development.");
        }

        logger.LogWarning(
            "Using an ephemeral ECDSA command signing key in Development. Enrolled agents must re-enroll after restart");
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }
}
