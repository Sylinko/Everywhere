using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Activation;

/// <summary>Submits a bounded activation to the existing Main process.</summary>
[RpcContract(6)]
public interface IApplicationActivationRpc
{
    /// <summary>Returns Accepted only after Main has taken ownership of the request.</summary>
    /// <param name="request">A closed activation command, without arbitrary routes or executable arguments.</param>
    /// <param name="cancellationToken">Cancels the request before ownership transfers.</param>
    /// <returns>Queue acceptance, never confirmation of OAuth completion.</returns>
    [RpcMethod(1)]
    ValueTask<ApplicationActivationResponse> ActivateAsync(ApplicationActivationRequest request, CancellationToken cancellationToken = default);
}