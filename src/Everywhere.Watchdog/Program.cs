using System.IO.Pipes;
using System.Text;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Watchdog;
#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
#endif

namespace Everywhere.Watchdog;

#if WINDOWS
[SupportedOSPlatform("windows6.0.6000")]
#endif
public static class Program
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("No pipe name was provided.");
            return 2;
        }

        NamedPipeClientStream? stream = null;
        RpcConnection? connection = null;
        WatchdogRpcService? service = null;
        try
        {
            stream = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(ConnectTimeout);
            await stream.ConnectAsync(deadline.Token).ConfigureAwait(false);

#if WINDOWS
            if (!PInvoke.GetNamedPipeServerProcessId(stream.SafePipeHandle, out var mainProcessId))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            service = WatchdogRpcService.Create(mainProcessId);
#else
            service = WatchdogRpcService.Create();
#endif

            connection = new RpcConnection(stream, isServer: true);
            stream = null;
            var localIdentity = RpcRuntimeIdentity.CreateCurrent(RpcPeerNames.Watchdog);
            connection.RegisterRequestHandler<RpcHandshake, RpcHandshakeAck>(
                RpcProtocolConstants.HandshakeOperationId,
                (handshake, _) =>
                {
                    var response = RpcHandshakeValidator.Validate(handshake, ProcessRole.Main, localIdentity);
                    if (!response.Accepted)
                    {
                        connection.RequestGracefulShutdown();
                    }

                    return ValueTask.FromResult(response);
                });
            WatchdogRpcBinding.Bind(connection, service);

            // ReSharper disable once MethodSupportsCancellation
            connection.Start();

            Console.WriteLine("Watchdog connected to Main.");
            try
            {
                await connection.Completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await Console.Error.WriteLineAsync($"Watchdog connection ended: {exception.Message}");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Timed out while connecting to Main.");
            return 2;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Watchdog startup failed: {exception.Message}");
            return 1;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            service?.Dispose();
        }
    }
}