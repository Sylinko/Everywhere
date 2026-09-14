using System.Diagnostics.CodeAnalysis;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Owns one chat's service-independent remote visual Context and target-validity state.
/// </summary>
/// <remarks>
/// Construction never contacts the Automation Host. <see cref="ChatVisualService" /> lazily attaches the current connection
/// and remote Context when an operation first needs it.
/// </remarks>
public sealed class ChatVisualState : IDisposable
{
    internal SemaphoreSlim ConnectionGate { get; } = new(1, 1);

    private readonly Lock _gate = new();
    private RpcConnection? _connection;
    private RemoteVisualContext? _context;
    private bool _isReobservationRequired;
    private bool _isDisposed;

    /// <summary>Creates a lazy state object for a chat.</summary>
    public ChatVisualState()
    {
    }

    /// <summary>Releases the currently attached remote Context, if one was created.</summary>
    public void Dispose()
    {
        RemoteVisualContext? context;
        lock (_gate)
        {
            if (_isDisposed) return;

            _isDisposed = true;
            context = _context;
            _context = null;
            _connection = null;
        }

        context?.Dispose();
    }

    internal bool TryGetContext(RpcConnection connection, [NotNullWhen(true)] out RemoteVisualContext? context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (ReferenceEquals(connection, _connection) && _context is not null)
            {
                context = _context;
                return true;
            }

            context = null;
            return false;
        }
    }

    internal RemoteVisualContext? ReplaceContext(RpcConnection connection, RemoteVisualContext replacement)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var previous = _context;
            _isReobservationRequired = previous is not null;
            _connection = connection;
            _context = replacement;
            return previous;
        }
    }

    internal void PublishTargets(RemoteVisualContext context)
    {
        lock (_gate)
        {
            if (_isDisposed || !ReferenceEquals(context, _context)) return;
            _isReobservationRequired = false;
        }
    }

    internal void Invalidate(RemoteVisualContext context)
    {
        lock (_gate)
        {
            if (_isDisposed || !ReferenceEquals(context, _context)) return;
            _isReobservationRequired = true;
        }
    }

    internal void ValidateTargets(RemoteVisualContext context)
    {
        lock (_gate)
        {
            if (_isDisposed || !ReferenceEquals(context, _context) || _isReobservationRequired)
            {
                throw new VisualContextResetException(ChatVisualService.ResetNotice);
            }
        }
    }
}