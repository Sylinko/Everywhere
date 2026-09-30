using System.IO.Pipes;
using System.Security.Principal;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Activation;

/// <summary>Forwards one activation and waits for Main to accept ownership.</summary>
public static class ApplicationActivationClient
{
    /// <summary>
    /// Retries only connection establishment. After authentication starts, failure is
    /// surfaced without replay because Main may already have accepted the request.
    /// </summary>
    public static async Task<ApplicationActivationStatus> SendAsync(
        string endpoint,
        ApplicationActivationRequest request,
        INamedPipePeerVerifier peerVerifier)
    {
        var identity = RpcRuntimeIdentity.CreateCurrent(RpcPeerNames.ApplicationActivation);
        await using var pipe = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await pipe.ConnectAsync(3000).ConfigureAwait(false);
                break;
            }
            catch (TimeoutException) when (attempt < 3)
            {
                await Task.Delay(250 * attempt).ConfigureAwait(false);
            }
        }

        peerVerifier.VerifyServer(pipe);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = new RpcConnection(pipe, isServer: false, new RpcConnectionOptions { MaximumFramePayloadBytes = 64 * 1024 });
        connection.Start(deadline.Token);
        var handshake = await connection.PerformHandshakeAsync(
            new RpcHandshake
            {
                AssemblyInformationalVersion = identity.AssemblyInformationalVersion,
                Role = identity.WireName,
                ProcessId = identity.ProcessId,
                DesktopSessionId = identity.DesktopSessionId
            },
            deadline.Token).ConfigureAwait(false);
        RpcHandshakeValidator.ValidateAcceptedPeer(handshake, ProcessRole.Main, identity);
        var response = await new ApplicationActivationRpcClient(connection).ActivateAsync(request, deadline.Token).ConfigureAwait(false);
        return response.Status;
    }
}