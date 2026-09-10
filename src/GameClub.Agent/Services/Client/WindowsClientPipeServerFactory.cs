using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using GameClub.Contracts.Client;

namespace GameClub.Agent.Services.Client;

public sealed class WindowsClientPipeServerFactory : IClientPipeServerFactory
{
    public NamedPipeServerStream Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("GameClub Client IPC requires Windows named pipes.");
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            ClientPipeProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
            inBufferSize: ClientPipeProtocol.MaximumMessageBytes,
            outBufferSize: ClientPipeProtocol.MaximumMessageBytes,
            security);
    }
}
