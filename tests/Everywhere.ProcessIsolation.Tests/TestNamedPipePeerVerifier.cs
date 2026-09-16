using System.IO.Pipes;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Tests;

internal sealed class TestNamedPipePeerVerifier : INamedPipePeerVerifier
{
    public static TestNamedPipePeerVerifier Instance { get; } = new();

    private TestNamedPipePeerVerifier()
    {
    }

    public void VerifyClient(NamedPipeServerStream stream)
    {
    }

    public void VerifyServer(NamedPipeClientStream stream)
    {
    }
}
