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
    public static Task<int> RunAsync(HostsControlOperation operation, CancellationToken cancellationToken = default) => operation switch
    {
        HostsControlOperation.Start => Task.FromResult(StartHosts()),
        HostsControlOperation.Stop => StopHostsAsync(cancellationToken),
        HostsControlOperation.Install => Task.FromResult(ReportUnavailable("install", "Windows Scheduled Task integration is not implemented yet.")),
        HostsControlOperation.Uninstall => Task.FromResult(ReportUnavailable("uninstall", "Windows Scheduled Task integration is not implemented yet.")),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
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
            return 1;
        }

        var exitCode = 0;
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
                    exitCode = 1;
                    continue;
                }

                process.Dispose();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Everywhere Hosts Control failed to start the {ProcessRoleNames.ToWireName(role)} Host: {exception.Message}");
                exitCode = 1;
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Requests Main to stop both Host roles. The controller never connects to a
    /// role endpoint itself, because doing so would compete with Main's primary
    /// lifetime lease. Main's aggregate response is the explicit confirmation.
    /// </summary>
    private static async Task<int> StopHostsAsync(CancellationToken cancellationToken)
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
            connection = new RpcConnection(stream, isServer: false);
            connection.Start(deadline.Token);

            var handshake = await connection.PerformHandshakeAsync(
                    new RpcHandshake
                    {
                        AssemblyInformationalVersion = localIdentity.AssemblyInformationalVersion,
                        Role = MainHostControlRpcOperations.ControllerWireName,
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
                return 1;
            }

            return response.Succeeded ? 0 : 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Everywhere Hosts Control stop was cancelled or timed out.");
            return 2;
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
            return 2;
        }
        catch (RpcProtocolException exception)
        {
            await Console.Error.WriteLineAsync($"Everywhere Hosts Control stop failed protocol validation: {exception.Message}");
            return 2;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> ReportMainUnavailableAsync(
        string desktopSessionId,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        var endpointsGone = await EndpointPresenceProbe
            .WaitForRolesToDisappearAsync(desktopSessionId, StopTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (endpointsGone)
        {
            return 0;
        }

        await Console.Error.WriteLineAsync(
            detail is null ?
                "Everywhere Hosts Control could not reach the running Main process while Host endpoints are still present." :
                $"Everywhere Hosts Control could not reach the running Main process while Host endpoints are still present: {detail}");
        return 2;
    }

    private static int ReportUnavailable(string operation, string reason)
    {
        Console.Error.WriteLine($"Everywhere Hosts Control '{operation}' is unavailable: {reason}");
        return 2;
    }
}