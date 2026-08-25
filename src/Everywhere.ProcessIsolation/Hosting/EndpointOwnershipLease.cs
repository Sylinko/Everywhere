using System.Security.Cryptography;
using System.Text;
using Everywhere.Utilities;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Owns the non-Windows endpoint lock used by the Host shell. Windows uses the
/// named-pipe first-instance flag instead, so this lease is only created there
/// when the platform does not provide equivalent pipe ownership semantics.
/// </summary>
internal sealed class EndpointOwnershipLease : IDisposable
{
    private FileStream? _stream;

    private EndpointOwnershipLease(FileStream stream) => _stream = stream;

    /// <summary>Attempts to acquire the lock corresponding to an endpoint name.</summary>
    public static EndpointOwnershipLease? TryAcquire(string endpoint)
    {
        var path = GetLockPath(endpoint);
        try
        {
            return new EndpointOwnershipLease(
                new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether a non-Windows Host currently owns the endpoint lock without
    /// taking a pipe connection or changing the endpoint state.
    /// </summary>
    public static bool IsHeld(string endpoint)
    {
        var path = GetLockPath(endpoint);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Releases the process-wide endpoint lease.</summary>
    public void Dispose()
    {
        DisposeHelper.DisposeToDefault(ref _stream);
    }

    private static string GetLockPath(string endpoint)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Everywhere", "rpc-owners");
        Directory.CreateDirectory(directory);
        var endpointHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
        return Path.Combine(directory, $"{endpointHash}.lock");
    }
}