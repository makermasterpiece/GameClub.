using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameClub.Server.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GameClub.Server.Tests.Security;

public sealed class ServerSigningKeyProviderTests
{
    [Fact]
    public void PasswordProtectedPfxCanBeLoadedWithoutExportingPrivateParametersOnWindows()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=GameClub disposable signing test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var path = Path.Combine(Path.GetTempPath(), $"gameclub-signing-test-{Guid.NewGuid():N}.pfx");
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
            using var provider = new ServerSigningKeyProvider(Options.Create(new SecurityOptions
            {
                CommandSigningCertificatePath = path,
                CommandSigningCertificatePassword = password
            }), new ProductionEnvironment(), NullLogger<ServerSigningKeyProvider>.Instance);

            Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), provider.PublicKeyBase64);
            Assert.Equal("ES256", provider.JwtSigningCredentials.Algorithm);
            Assert.Equal(256, provider.JwtValidationKey.KeySize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "GameClub.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
