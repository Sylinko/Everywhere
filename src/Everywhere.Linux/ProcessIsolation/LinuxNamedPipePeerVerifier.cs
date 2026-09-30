using System.IO.Pipes;
using System.Runtime.InteropServices;
using Everywhere.ProcessIsolation.Rpc;
using Microsoft.Win32.SafeHandles;

namespace Everywhere.Linux.ProcessIsolation;

/// <summary>Authenticates activation peers using Unix socket credentials and their executable image.</summary>
public sealed partial class LinuxNamedPipePeerVerifier : INamedPipePeerVerifier
{
    /// <inheritdoc />
    public void VerifyClient(NamedPipeServerStream stream) => VerifyPeer(stream.SafePipeHandle);

    /// <inheritdoc />
    public void VerifyServer(NamedPipeClientStream stream) => VerifyPeer(stream.SafePipeHandle);

    private static unsafe void VerifyPeer(SafePipeHandle handle)
    {
        var hasHandleReference = false;
        try
        {
            handle.DangerousAddRef(ref hasHandleReference);
            var credentials = default(PeerCredentials);
            var length = (uint)sizeof(PeerCredentials);
            if (GetSocketOption(handle.DangerousGetHandle().ToInt32(), 1, 17, &credentials, ref length) != 0 ||
                length != sizeof(PeerCredentials) || credentials.ProcessId <= 0 || credentials.UserId != GetEffectiveUserId())
            {
                throw new RpcProtocolException("The activation peer's Unix credentials could not be verified.");
            }

            var peerImage = File.ResolveLinkTarget($"/proc/{credentials.ProcessId}/exe", returnFinalTarget: true)?.FullName;
            var currentImage = File.ResolveLinkTarget("/proc/self/exe", returnFinalTarget: true)?.FullName;
            if (currentImage is null || !string.Equals(peerImage, currentImage, StringComparison.Ordinal))
            {
                throw new RpcProtocolException("The activation peer is not running the expected application image.");
            }
        }
        finally
        {
            if (hasHandleReference) handle.DangerousRelease();
        }
    }

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static unsafe partial int GetSocketOption(int socket, int level, int option, void* value, ref uint length);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        public int ProcessId { get; }
        public uint UserId { get; }
        public uint GroupId { get; }
    }
}