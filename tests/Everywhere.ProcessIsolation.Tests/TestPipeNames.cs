namespace Everywhere.ProcessIsolation.Tests;

internal static class TestPipeNames
{
    // Named pipes use Unix-domain sockets on macOS. Keep the generated name short
    // enough for the 104-character socket path after the system temp-path prefix.
    public static string Create() => $"evtest-{Guid.NewGuid():N}"[..23];
}
