using System.IO.Pipes;

namespace GameClub.Agent.Services.Client;

public interface IClientPipeServerFactory
{
    NamedPipeServerStream Create();
}
