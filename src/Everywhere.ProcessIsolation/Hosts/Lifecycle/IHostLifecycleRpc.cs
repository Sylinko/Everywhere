using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Lifecycle;

/// <summary>
/// Source-level lifecycle contract shared by Main and each role Host.
///
/// Contract ID 1 and method IDs 1-3 are wire ABI values. Requests travel from
/// Main to the Host over an already authenticated <see cref="RpcConnection"/>;
/// the Host returns a typed response. The connection nonce is transport state,
/// so it is deliberately not repeated in these DTOs. The RPC source generator
/// emits the client and binder directly from this interface.
/// </summary>
[RpcContract(1)]
public interface IHostLifecycleRpc
{
    /// <summary>
    /// Reads a point-in-time lifecycle snapshot. This method has no state transition
    /// and is safe to use while the Host is Listening, Connected, or Draining.
    /// </summary>
    /// <param name="request">Empty request reserved for future query fields.</param>
    /// <param name="cancellationToken">Cancels the local dispatch before a response is sent.</param>
    /// <returns>The Host role, state, process ID, and monotonic observation timestamp.</returns>
    [RpcMethod(1)]
    ValueTask<HostStatusResponse> GetStatusAsync(
        HostStatusRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the Host to stop accepting new work and drain before an update.
    /// The transition is idempotent; only the first transition is reported as accepted.
    /// </summary>
    /// <param name="request">Optional diagnostic reason for the update operation.</param>
    /// <param name="cancellationToken">Cancels the local dispatch before a response is sent.</param>
    /// <returns>Whether this request performed the transition and, if not, why.</returns>
    [RpcMethod(2)]
    ValueTask<HostOperationResponse> PrepareForUpdateAsync(
        PrepareForUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cooperative shutdown after the response and previously queued
    /// already accepted FIFO frames have drained. The <c>Restart</c> bit is an instruction
    /// for the parent coordinator; the Host itself does not launch arbitrary processes.
    /// </summary>
    /// <param name="request">Shutdown intent and optional replacement indication.</param>
    /// <param name="cancellationToken">Cancels the local dispatch before a response is sent.</param>
    /// <returns>Whether this request performed the transition and, if not, why.</returns>
    [RpcMethod(3)]
    ValueTask<HostOperationResponse> ShutdownAsync(
        ShutdownRequest request,
        CancellationToken cancellationToken = default);
}