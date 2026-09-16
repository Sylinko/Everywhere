using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Everywhere.ProcessIsolation.Rpc;
using Microsoft.Win32.SafeHandles;

namespace Everywhere.Mac.ProcessIsolation;

/// <summary>Validates the user and application image of a macOS named-pipe peer.</summary>
internal sealed partial class MacNamedPipePeerVerifier : INamedPipePeerVerifier
{
    private const int SolLocal = 0;
    private const int LocalPeerPid = 2;
    private const int ProcessPathBufferSize = 4096;

    public static MacNamedPipePeerVerifier Instance { get; } = new();

    private MacNamedPipePeerVerifier()
    {
    }

    /// <inheritdoc />
    public void VerifyClient(NamedPipeServerStream stream) => VerifyPeer(stream.SafePipeHandle, "client");

    /// <inheritdoc />
    public void VerifyServer(NamedPipeClientStream stream) => VerifyPeer(stream.SafePipeHandle, "server");

    private static unsafe void VerifyPeer(SafePipeHandle pipe, string peerDescription)
    {
        var hasHandleReference = false;
        try
        {
            pipe.DangerousAddRef(ref hasHandleReference);
            var socket = pipe.DangerousGetHandle().ToInt32();

            if (GetPeerEffectiveIdentity(socket, out var peerUserId, out _) != 0)
            {
                throw CreateProtocolException(peerDescription, Marshal.GetLastPInvokeError());
            }

            if (peerUserId != GetEffectiveUserId())
            {
                throw new RpcProtocolException($"The named-pipe {peerDescription} belongs to a different user.");
            }

            var processId = 0;
            var processIdSize = (uint)sizeof(int);
            if (GetSocketOption(socket, SolLocal, LocalPeerPid, &processId, ref processIdSize) != 0)
            {
                throw CreateProtocolException(peerDescription, Marshal.GetLastPInvokeError());
            }

            if (processId <= 0)
            {
                throw new RpcProtocolException($"The named-pipe {peerDescription} returned an invalid process ID.");
            }

            VerifyImage(processId, peerDescription);
        }
        finally
        {
            if (hasHandleReference)
            {
                pipe.DangerousRelease();
            }
        }
    }

    private static unsafe void VerifyImage(int processId, string peerDescription)
    {
        var buffer = stackalloc byte[ProcessPathBufferSize];
        var length = GetProcessPath(processId, buffer, ProcessPathBufferSize);
        if (length <= 0)
        {
            throw CreateProtocolException(peerDescription, Marshal.GetLastPInvokeError());
        }

        var currentImagePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentImagePath))
        {
            throw new RpcProtocolException("The current executable image path is unavailable for peer validation.");
        }

        var expectedPath = GetCanonicalPath(Path.GetFullPath(currentImagePath), peerDescription);
        var actualPath = GetCanonicalPath(Path.GetFullPath(Encoding.UTF8.GetString(buffer, length)), peerDescription);
        if (!string.Equals(expectedPath, actualPath, StringComparison.Ordinal))
        {
            throw new RpcProtocolException($"The named-pipe {peerDescription} is not running the expected application image.");
        }
    }

    private static RpcProtocolException CreateProtocolException(string peerDescription, int error) =>
        new($"The named-pipe {peerDescription} identity could not be verified: {new Win32Exception(error).Message}");

    private static string GetCanonicalPath(string path, string peerDescription)
    {
        var canonicalPath = ResolvePath(path, 0);
        if (canonicalPath == 0)
        {
            throw CreateProtocolException(peerDescription, Marshal.GetLastPInvokeError());
        }

        try
        {
            return Marshal.PtrToStringUTF8(canonicalPath) ??
                throw new RpcProtocolException($"The named-pipe {peerDescription} executable path could not be decoded.");
        }
        finally
        {
            Free(canonicalPath);
        }
    }

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "getpeereid", SetLastError = true)]
    private static partial int GetPeerEffectiveIdentity(int socket, out uint effectiveUserId, out uint effectiveGroupId);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "getsockopt", SetLastError = true)]
    private static unsafe partial int GetSocketOption(int socket, int level, int optionName, void* optionValue, ref uint optionLength);

    [LibraryImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidpath", SetLastError = true)]
    private static unsafe partial int GetProcessPath(int processId, byte* buffer, uint bufferSize);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ResolvePath(string path, nint resolvedPath);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "free")]
    private static partial void Free(nint pointer);
}