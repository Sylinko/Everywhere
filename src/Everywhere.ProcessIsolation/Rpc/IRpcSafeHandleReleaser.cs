namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Accepts nonblocking release records produced by RPC SafeHandles.
/// </summary>
/// <remarks>
/// Returning <see langword="true" /> means either the record was accepted for the handle's original connection or that connection has already ended and its Host-side session owns cleanup.
/// </remarks>
public interface IRpcSafeHandleReleaser
{
    /// <summary>Queues exactly one remote-resource release without waiting for peer execution.</summary>
    bool TryQueueRelease(long resourceId);
}