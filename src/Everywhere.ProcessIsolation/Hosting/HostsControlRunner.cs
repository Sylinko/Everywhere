using System.Diagnostics;
using System.IO.Pipes;
using Everywhere.ProcessIsolation.Hosts.Control;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Executes the short-lived <c>--hosts-control</c> command before application
/// initialization. The controller exposes only closed product operations and
/// never accepts an executable path or arbitrary process arguments.
/// </summary>
public static class HostsControlRunner
{
    private static readonly TimeSpan StopConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Asynchronously executes one closed Hosts-control operation.</summary>
    /// <param name="command">Validated operation and its fixed confirmation switches.</param>
    /// <param name="platform">Platform-specific service-mode controller.</param>
    /// <param name="peerVerifier">Platform policy used when the stop operation connects to Main.</param>
    /// <param name="cancellationToken">Cancels bounded controller work.</param>
    public static Task<int> RunAsync(
        HostsControlCommand command,
        IHostsControlPlatform platform,
        INamedPipePeerVerifier peerVerifier,
        CancellationToken cancellationToken = default) => command.Operation switch
    {
        HostsControlOperation.Start => Task.FromResult(StartServiceModeHosts(platform)),
        HostsControlOperation.Stop => StopHostsAsync(peerVerifier, cancellationToken),
        HostsControlOperation.Install => Task.FromResult(ReportPlatformResult("install", platform.InstallServiceMode(command))),
        HostsControlOperation.Uninstall => Task.FromResult(ReportPlatformResult("uninstall", platform.UninstallServiceMode())),
        HostsControlOperation.Launch => Task.FromResult(StartHosts()),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.Operation, null)
    };

    /// <summary>
    /// Starts exactly the two fixed Host roles using the current executable and
    /// integrity level. Endpoint ownership, rather than process creation success,
    /// decides which candidate becomes the live Host.
    /// </summary>
    private static int StartHosts()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Console.Error.WriteLine("Everywhere Hosts Control could not resolve the current executable path.");
            return HostsControlExitCodes.Failure;
        }

        var exitCode = HostsControlExitCodes.Success;
        foreach (var role in new[] { ProcessRole.Input, ProcessRole.Automation })
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add($"--process-role={ProcessRoleNames.ToWireName(role)}");

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    Console.Error.WriteLine($"Everywhere Hosts Control could not start the {ProcessRoleNames.ToWireName(role)} Host.");
                    exitCode = HostsControlExitCodes.Failure;
                    continue;
                }

                process.Dispose();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Everywhere Hosts Control failed to start the {ProcessRoleNames.ToWireName(role)} Host: {exception.Message}");
                exitCode = HostsControlExitCodes.Failure;
            }
        }

        return exitCode;
    }

    private static int StartServiceModeHosts(IHostsControlPlatform platform)
    {
        var result = platform.StartServiceMode(Process.GetCurrentProcess().SessionId);
        if (!string.IsNullOrWhiteSpace(result.DiagnosticDetail))
        {
            var writer = result.Outcome is HostsControlPlatformOutcome.Succeeded or HostsControlPlatformOutcome.DirectLaunchRequired ?
                Console.Out :
                Console.Error;
            writer.WriteLine(result.DiagnosticDetail);
        }

        if (result.Outcome is HostsControlPlatformOutcome.Succeeded)
        {
            return HostsControlExitCodes.Success;
        }

        var directExitCode = StartHosts();
        if (directExitCode != HostsControlExitCodes.Success)
        {
            Console.Error.WriteLine("Everywhere Hosts ordinary fallback also failed.");
            return HostsControlExitCodes.Failure;
        }

        if (result.Outcome is HostsControlPlatformOutcome.DirectLaunchRequired)
        {
            return HostsControlExitCodes.Success;
        }

        if (platform.IsDirectLaunchEquivalentToServiceMode)
        {
            Console.Out.WriteLine("Everywhere Hosts started directly with service-equivalent capabilities after service-mode launch failed.");
            return HostsControlExitCodes.EquivalentFallbackStarted;
        }

        Console.Out.WriteLine("Everywhere Hosts started directly with reduced capabilities after service-mode launch failed.");
        return HostsControlExitCodes.LimitedFallbackStarted;
    }

    /// <summary>
    /// Requests Main to stop both Host roles. The controller never connects to a
    /// role endpoint itself, because doing so would compete with Main's primary
    /// lifetime lease. Main's aggregate response is the explicit confirmation.
    /// </summary>
    private static async Task<int> StopHostsAsync(INamedPipePeerVerifier peerVerifier, CancellationToken cancellationToken)
    {
        var localIdentity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
        var endpoint = ProcessRoleNames.GetMainControlEndpoint(localIdentity.DesktopSessionId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StopTimeout);

        await using var stream = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        RpcConnection? connection = null;
        try
        {
            await stream.ConnectAsync((int)StopConnectTimeout.TotalMilliseconds, deadline.Token).ConfigureAwait(false);
            peerVerifier.VerifyServer(stream);
            connection = new RpcConnection(stream, isServer: false);
            connection.Start(deadline.Token);

            var handshake = await connection.PerformHandshakeAsync(
                    new RpcHandshake
                    {
                        AssemblyInformationalVersion = localIdentity.AssemblyInformationalVersion,
                        Role = RpcPeerNames.HostsControl,
                        ProcessId = localIdentity.ProcessId,
                        DesktopSessionId = localIdentity.DesktopSessionId
                    },
                    deadline.Token)
                .ConfigureAwait(false);
            RpcHandshakeValidator.ValidateAcceptedPeer(handshake, ProcessRole.Main, localIdentity);

            var response = await new MainHostControlRpcClient(connection)
                .StopHostsAsync(new StopHostsRequest(), deadline.Token)
                .AsTask()
                .ConfigureAwait(false);
            if (!response.Succeeded)
            {
                await Console.Error.WriteLineAsync(
                    $"Everywhere Hosts Control stop was not confirmed: input={response.InputHostAcknowledged}, automation={response.AutomationHostAcknowledged}.");
            }

            try
            {
                await connection.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception) when (connection.Completion.IsCompleted)
            {
                // The explicit response was already received. A graceful transport
                // completion may surface EOF after Main has released the endpoint.
            }

            var endpointsGone = await EndpointPresenceProbe
                .WaitForRolesToDisappearAsync(localIdentity.DesktopSessionId, StopTimeout, deadline.Token)
                .ConfigureAwait(false);
            if (!endpointsGone)
            {
                await Console.Error.WriteLineAsync("Everywhere Hosts Control completed Main coordination, but a Host endpoint remained present.");
                return HostsControlExitCodes.Failure;
            }

            return response.Succeeded ? HostsControlExitCodes.Success : HostsControlExitCodes.Failure;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Everywhere Hosts Control stop was cancelled or timed out.");
            return HostsControlExitCodes.Unavailable;
        }
        catch (TimeoutException)
        {
            return await ReportMainUnavailableAsync(localIdentity.DesktopSessionId, deadline.Token).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return await ReportMainUnavailableAsync(localIdentity.DesktopSessionId, deadline.Token, exception.Message).ConfigureAwait(false);
        }
        catch (RpcRemoteException exception)
        {
            await Console.Error.WriteLineAsync($"Everywhere Hosts Control stop was rejected: {exception.Code}.");
            return HostsControlExitCodes.Unavailable;
        }
        catch (RpcProtocolException exception)
        {
            await Console.Error.WriteLineAsync($"Everywhere Hosts Control stop failed protocol validation: {exception.Message}");
            return HostsControlExitCodes.Unavailable;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> ReportMainUnavailableAsync(string desktopSessionId, CancellationToken cancellationToken, string? detail = null)
    {
        var endpointsGone = await EndpointPresenceProbe
            .WaitForRolesToDisappearAsync(desktopSessionId, StopTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (endpointsGone)
        {
            return HostsControlExitCodes.Success;
        }

        await Console.Error.WriteLineAsync(
            detail is null ?
                "Everywhere Hosts Control could not reach the running Main process while Host endpoints are still present." :
                $"Everywhere Hosts Control could not reach the running Main process while Host endpoints are still present: {detail}");
        return HostsControlExitCodes.Unavailable;
    }

    private static int ReportPlatformResult(string operation, HostsControlPlatformResult result)
    {
        var isSuccess = result.Outcome is HostsControlPlatformOutcome.Succeeded;
        var writer = isSuccess ? Console.Out : Console.Error;
        writer.WriteLine(
            string.IsNullOrWhiteSpace(result.DiagnosticDetail) ?
                $"Everywhere Hosts Control '{operation}' {(isSuccess ? "completed" : "failed")}." :
                result.DiagnosticDetail);
        return result.Outcome switch
        {
            HostsControlPlatformOutcome.Succeeded => HostsControlExitCodes.Success,
            HostsControlPlatformOutcome.Conflict => HostsControlExitCodes.Conflict,
            HostsControlPlatformOutcome.Failed => HostsControlExitCodes.Failure,
            _ => HostsControlExitCodes.Unavailable
        };
    }
}