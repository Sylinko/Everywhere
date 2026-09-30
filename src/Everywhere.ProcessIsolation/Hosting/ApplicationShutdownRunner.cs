using System.Diagnostics;
using System.IO.Pipes;
using Everywhere.ProcessIsolation.Hosts.Control;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Requests normal application shutdown from the installed executable's Main-control endpoint.
/// This is a same-build, same-user/session operation; installation-wide file checks remain the installer's responsibility.
/// </summary>
public static class ApplicationShutdownRunner
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for Main process termination, not merely an RPC reply or endpoint disappearance.
    /// The caller must execute the installed image before replacing it; arbitrary paths and process IDs are not accepted.
    /// </summary>
    public static async Task<int> RunAsync(INamedPipePeerVerifier peerVerifier, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return ReportFailure("Installation shutdown is supported only on Windows.");
        var identity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ShutdownTimeout);
        try
        {
            await using var stream = new NamedPipeClientStream(
                ".",
                ProcessRoleNames.GetMainControlEndpoint(identity.DesktopSessionId),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await stream.ConnectAsync(3000, deadline.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return AreEndpointsAbsent(identity.DesktopSessionId) ?
                    HostsControlExitCodes.Success :
                    ReportFailure("Main is still present or unavailable for cooperative shutdown.");
            }

            peerVerifier.VerifyServer(stream);
            await using var connection = new RpcConnection(stream, isServer: false);
            connection.Start(deadline.Token);
            var handshake = await connection.PerformHandshakeAsync(
                new RpcHandshake
                {
                    AssemblyInformationalVersion = identity.AssemblyInformationalVersion,
                    Role = RpcPeerNames.HostsControl,
                    ProcessId = identity.ProcessId,
                    DesktopSessionId = identity.DesktopSessionId
                },
                deadline.Token).ConfigureAwait(false);
            RpcHandshakeValidator.ValidateAcceptedPeer(handshake, ProcessRole.Main, identity);

            // Open the handle before requesting shutdown so subsequent waits cannot follow a reused PID.
            if (handshake.ProcessId > int.MaxValue) return ReportFailure("The Main process ID is invalid.");
            using var main = Process.GetProcessById((int)handshake.ProcessId);
            var mainHandle = main.SafeHandle;
            if (main.Id == Environment.ProcessId || mainHandle.IsInvalid)
            {
                return ReportFailure("The Main process identity could not be retained.");
            }

            try
            {
                await new MainHostControlRpcClient(connection)
                    .ShutdownApplicationAsync(new ShutdownApplicationRequest(), deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A normal concurrent exit may race the reply. Only actual termination below proves success.
                await Console.Error.WriteLineAsync("The shutdown connection closed before acknowledgement; waiting for Main to exit.");
            }

            await main.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (!await EndpointPresenceProbe.WaitForRolesToDisappearAsync(identity.DesktopSessionId, ShutdownTimeout, deadline.Token)
                    .ConfigureAwait(false) ||
                !AreEndpointsAbsent(identity.DesktopSessionId))
            {
                return ReportFailure("Main exited, but application endpoints remain present.");
            }

            return HostsControlExitCodes.Success;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ReportFailure("Application shutdown was cancelled or timed out.");
        }
        catch (Exception exception)
        {
            return ReportFailure($"Application shutdown could not be confirmed: {exception.Message}");
        }
    }

    private static bool AreEndpointsAbsent(string desktopSessionId) =>
        !EndpointPresenceProbe.IsPresent(ProcessRoleNames.GetApplicationActivationEndpoint(desktopSessionId)) &&
        !EndpointPresenceProbe.IsPresent(ProcessRoleNames.GetMainControlEndpoint(desktopSessionId)) &&
        !EndpointPresenceProbe.IsPresent(ProcessRoleNames.GetDefaultEndpoint(ProcessRole.Input, desktopSessionId)) &&
        !EndpointPresenceProbe.IsPresent(ProcessRoleNames.GetDefaultEndpoint(ProcessRole.Automation, desktopSessionId));

    private static int ReportFailure(string message)
    {
        Console.Error.WriteLine($"Everywhere application shutdown: {message}");
        return HostsControlExitCodes.Unavailable;
    }
}