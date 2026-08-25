using System.Runtime.InteropServices;
using Everywhere.ProcessIsolation.Roles;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Non-owning Host endpoint probe used after stop, restart, and update draining.
/// It never creates a client pipe, because a client connection would compete with
/// Main's lifetime lease. Windows queries the named-pipe object; Unix checks the
/// shell's exclusive ownership lock.
/// </summary>
public static partial class EndpointPresenceProbe
{
    private const int ErrorPipeBusy = 231;
    private const int ErrorSemTimeout = 121;

    /// <summary>Waits until both fixed role endpoints are absent.</summary>
    public static async Task<bool> WaitForRolesToDisappearAsync(
        string desktopSessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var roles = new[] { ProcessRole.Input, ProcessRole.Automation };
        while (!deadline.IsCancellationRequested)
        {
            if (roles.All(role => !IsPresent(ProcessRoleNames.GetDefaultEndpoint(role, desktopSessionId))))
            {
                return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
        }

        return roles.All(role => !IsPresent(ProcessRoleNames.GetDefaultEndpoint(role, desktopSessionId)));
    }

    private static bool IsPresent(string endpoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return EndpointOwnershipLease.IsHeld(endpoint);
        }

        var pipePath = $@"\\.\pipe\{endpoint}";
        if (WaitNamedPipe(pipePath, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        return error is ErrorPipeBusy or ErrorSemTimeout;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeout);
}