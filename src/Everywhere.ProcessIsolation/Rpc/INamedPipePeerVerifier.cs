using System.IO.Pipes;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>Authenticates the operating-system identity of a connected named-pipe peer.</summary>
public interface INamedPipePeerVerifier
{
    /// <summary>Verifies the client connected to a server pipe.</summary>
    void VerifyClient(NamedPipeServerStream stream);

    /// <summary>Verifies the server to which a client pipe is connected.</summary>
    void VerifyServer(NamedPipeClientStream stream);
}