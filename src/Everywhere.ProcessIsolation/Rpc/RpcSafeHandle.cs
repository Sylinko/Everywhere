namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Owns one positive remote resource identifier for the lifetime of its originating RPC connection.
/// </summary>
/// <remarks>
/// Explicit disposal and finalization use the same nonblocking release path. Connection teardown remains the fallback when a process exits without running finalizers or a release can no longer be delivered.
/// </remarks>
public abstract class RpcSafeHandle : SafeHandle
{
    /// <inheritdoc />
    public override bool IsInvalid => handle == 0;

    private readonly IRpcSafeHandleReleaser _releaser;

    /// <summary>Initializes ownership of one remote resource.</summary>
    protected RpcSafeHandle(long resourceId, IRpcSafeHandleReleaser releaser) : base(0, true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceId);
        SetHandle((nint)resourceId);
        _releaser = releaser;
    }

    /// <summary>
    /// Keeps this handle alive across an asynchronous RPC operation and exposes its remote identifier.
    /// </summary>
    public RpcSafeHandleLease AcquireLease()
    {
        var isAdded = false;
        DangerousAddRef(ref isAdded);
        if (!isAdded)
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        return new RpcSafeHandleLease(this, DangerousGetHandle());
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        try
        {
            return _releaser.TryQueueRelease(handle);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Holds a DangerousAddRef acquired from an <see cref="RpcSafeHandle" /> until an RPC operation finishes.
/// </summary>
public sealed class RpcSafeHandleLease : IDisposable
{
    /// <summary>Gets the remote identifier protected by this lease.</summary>
    public long ResourceId { get; }

    private RpcSafeHandle? _owner;

    internal RpcSafeHandleLease(RpcSafeHandle owner, long resourceId)
    {
        _owner = owner;
        ResourceId = resourceId;
    }

    /// <summary>Allows the owning SafeHandle to release after this operation stops using it.</summary>
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.DangerousRelease();
    }
}