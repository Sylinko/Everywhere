using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Everywhere.ProcessIsolation.Rpc;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Everywhere.Windows.ProcessIsolation;

/// <summary>Validates that a Windows named-pipe peer is executing the same application image as the current process.</summary>
internal sealed class WindowsNamedPipePeerVerifier : INamedPipePeerVerifier
{
    private const int MaximumImagePathLength = 32768;

    public static WindowsNamedPipePeerVerifier Instance { get; } = new();

    private WindowsNamedPipePeerVerifier()
    {
    }

    /// <inheritdoc />
    public void VerifyClient(NamedPipeServerStream stream)
    {
        if (!PInvoke.GetNamedPipeClientProcessId(stream.SafePipeHandle, out var processId))
        {
            throw CreateProtocolException("client", Marshal.GetLastPInvokeError());
        }

        VerifyImage(processId, "client");
    }

    /// <inheritdoc />
    public void VerifyServer(NamedPipeClientStream stream)
    {
        if (!PInvoke.GetNamedPipeServerProcessId(stream.SafePipeHandle, out var processId))
        {
            throw CreateProtocolException("server", Marshal.GetLastPInvokeError());
        }

        VerifyImage(processId, "server");
    }

    private static void VerifyImage(uint processId, string peerDescription)
    {
        var processHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        using var process = new SafeProcessHandle(processHandle, ownsHandle: true);
        if (process.IsInvalid)
        {
            throw CreateProtocolException(peerDescription, Marshal.GetLastPInvokeError());
        }

        var imagePathBuffer = new char[MaximumImagePathLength];
        var imagePathLength = (uint)imagePathBuffer.Length;
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, imagePathBuffer, ref imagePathLength))
        {
            throw CreateProtocolException(peerDescription, Marshal.GetLastPInvokeError());
        }

        var currentImagePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentImagePath))
        {
            throw new RpcProtocolException("The current executable image path is unavailable for peer validation.");
        }

        var expectedPath = Path.GetFullPath(currentImagePath);
        var actualPath = Path.GetFullPath(imagePathBuffer.AsSpan(0, checked((int)imagePathLength)).ToString());
        if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcProtocolException($"The named-pipe {peerDescription} is not running the expected application image.");
        }
    }

    private static RpcProtocolException CreateProtocolException(string peerDescription, int error) =>
        new($"The named-pipe {peerDescription} identity could not be verified: {new Win32Exception(error).Message}");
}