using System.Security.Cryptography;
using GameClub.Domain.Commands;
using GameClub.Domain.Security;
using GameClub.Agent.Models;

namespace GameClub.Agent.Security;

public interface IServerCommandSignatureVerifier
{
    bool Verify(SignedAgentCommandEnvelope envelope, StationCredentialData credential);
}

public sealed class ServerCommandSignatureVerifier : IServerCommandSignatureVerifier
{
    public bool Verify(SignedAgentCommandEnvelope envelope, StationCredentialData credential)
    {
        if (!string.Equals(
                credential.SignatureAlgorithm,
                CommandEnvelopeCryptography.SignatureAlgorithm,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(credential.ServerPublicKey),
                out _);
            return publicKey.KeySize == 256 && CommandEnvelopeCryptography.Verify(publicKey, envelope);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
