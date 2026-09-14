using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;
using Everywhere.Utilities;

namespace Everywhere.Automation;

/// <summary>
/// Represents one canonical platform visual element in a <see cref="VisualContext" /> identity domain.
/// </summary>
/// <remarks>
/// Concrete operations call the platform directly and use the platform's RPC timeout as their safety boundary. Element lifetime is owned by attachment, Snapshot, and Agent-turn <see cref="VisualElementRetention" /> batches rather than by individual operations.
/// </remarks>
public abstract class VisualElement
{
    /// <summary>
    /// Gets the stable platform identity of this element within its owning Context.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the immutable identity of this element within its owning Context.
    /// </summary>
    public VisualElementIdentity Identity { get; }

    /// <summary>
    /// Gets metadata accumulated by platform and projection stages for this active element incarnation.
    /// </summary>
    public VisualElementMetadata Metadata { get; } = new();

    /// <summary>
    /// Gets the Context that owns this element's logical identity domain.
    /// </summary>
    public VisualContext Context => Identity.Context;

    private bool _isReleased;

    /// <summary>
    /// Initializes an unretained platform element with its immutable Context identity.
    /// </summary>
    protected VisualElement(VisualElementIdentity identity, string id)
    {
        Identity = identity;
        Id = id;
    }

    /// <summary>
    /// Queries bounded scalar fields through this element's concrete platform implementation.
    /// </summary>
    public VisualElementQueryResult Query(VisualElementQueryRequest request)
    {
        EnsureUsable();
        try
        {
            return QueryCore(request);
        }
        catch (Exception exception) when (TryConvertPlatformException(exception, out var convertedException))
        {
            if (ReferenceEquals(exception, convertedException))
            {
                throw;
            }

            throw convertedException;
        }
    }

    /// <summary>
    /// Reads one bounded page of this element's textual content.
    /// </summary>
    /// <remarks>
    /// Offsets address the UTF-16 text exposed by this element. The operation is best effort over a live tree and does not promise cross-call immutability.
    /// A page may exceed <paramref name="maxCharacters" /> by one UTF-16 code unit rather than split a surrogate pair.
    /// </remarks>
    /// <param name="offset">The non-negative UTF-16 offset in the text exposed to the caller.</param>
    /// <param name="maxCharacters">The positive target maximum number of UTF-16 code units requested for this page.</param>
    public VisualElementTextReadResult ReadText(int offset = 0, int maxCharacters = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        EnsureUsable();
        try
        {
            return ReadTextCore(offset, maxCharacters);
        }
        catch (Exception exception) when (TryConvertPlatformException(exception, out var convertedException))
        {
            if (ReferenceEquals(exception, convertedException))
            {
                throw;
            }

            throw convertedException;
        }
    }

    /// <summary>
    /// Creates a lazy, single-use relation Enumerator through this element's concrete platform implementation.
    /// </summary>
    /// <param name="relation">The topological relation to enumerate.</param>
    /// <param name="request">The bounded scalar query applied to each yielded element.</param>
    /// <remarks>Recoverable provider failures are yielded once as terminal relation items. Creating the Enumerator performs no platform relation work.</remarks>
    public IVisualElementEnumerator CreateEnumerator(VisualElementRelation relation, VisualElementQueryRequest request)
    {
        EnsureUsable();
        return new RelationEnumerator(this, relation, request);
    }

    /// <summary>
    /// Invokes this element's semantic default action.
    /// </summary>
    public void Invoke() => ExecuteAction(default(Void), static (element, _) => element.InvokeCore());

    /// <summary>
    /// Replaces this element's editable scalar text.
    /// </summary>
    public void SetText(string text) => ExecuteAction(text, static (element, value) => element.SetTextCore(value));

    /// <summary>
    /// Sets keyboard focus to this element.
    /// </summary>
    public void Focus() => ExecuteAction(default(Void), static (element, _) => element.FocusCore());

    /// <summary>
    /// Sends one keyboard gesture to this element.
    /// </summary>
    public void SendKeyGesture(KeyGesture keyGesture) => ExecuteAction(keyGesture, static (element, value) => element.SendKeyGestureCore(value));

    /// <summary>
    /// Gets a bounded textual representation of this element's current selection.
    /// </summary>
    /// <param name="maxCharacters">The maximum number of UTF-16 characters to return.</param>
    /// <returns>The selected text or selected child labels, or <see langword="null" /> when no textual selection is available.</returns>
    public string? GetSelectedText(int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);
        EnsureUsable();
        try
        {
            return GetSelectedTextCore(maxCharacters);
        }
        catch (Exception exception) when (TryConvertPlatformException(exception, out var convertedException))
        {
            if (ReferenceEquals(exception, convertedException))
            {
                throw;
            }

            throw convertedException;
        }
    }

    /// <summary>
    /// Captures this visual element.
    /// </summary>
    public async Task<IVisualElementCapture> CaptureAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        try
        {
            return await CaptureCoreAsync(cancellationToken);
        }
        catch (Exception exception) when (TryConvertPlatformException(exception, out var convertedException))
        {
            if (ReferenceEquals(exception, convertedException))
            {
                throw;
            }

            throw convertedException;
        }
    }

    /// <summary>
    /// Retains this native visual identity in another <see cref="VisualContext" /> and returns that Context's canonical element.
    /// </summary>
    /// <remarks>
    /// This operation does not consume any existing owner. Concrete implementations must create an independently owned native
    /// reference when the destination does not already contain a canonical instance.
    /// </remarks>
    public VisualElement Adopt(VisualElementRetention destinationRetention)
    {
        EnsureUsable();
        ObjectDisposedException.ThrowIf(destinationRetention.IsDisposed, destinationRetention);
        if (ReferenceEquals(Context, destinationRetention.Context))
        {
            destinationRetention.Retain(this);
            return this;
        }

        try
        {
            return AdoptCore(destinationRetention);
        }
        catch (Exception exception) when (TryConvertPlatformException(exception, out var convertedException))
        {
            if (ReferenceEquals(exception, convertedException)) throw;
            throw convertedException;
        }
    }

    /// <summary>
    /// Queries bounded scalar fields through the concrete platform implementation.
    /// </summary>
    protected abstract VisualElementQueryResult QueryCore(VisualElementQueryRequest request);

    /// <summary>
    /// Reads one bounded text page through the concrete platform implementation.
    /// </summary>
    protected virtual VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters) =>
        new(null, null, new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null));

    /// <summary>
    /// Creates the low-level platform cursor when the public relation Enumerator is first advanced.
    /// </summary>
    /// <param name="relation">The topological relation to enumerate.</param>
    /// <param name="request">The bounded scalar query applied to each yielded element.</param>
    protected abstract IVisualElementCursor CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request);

    /// <summary>
    /// Invokes the concrete element's semantic default action.
    /// </summary>
    protected virtual void InvokeCore() => throw new NotSupportedException("This visual element does not support invocation.");

    /// <summary>
    /// Replaces editable scalar text through the concrete platform implementation.
    /// </summary>
    protected virtual void SetTextCore(string text) => throw new NotSupportedException("This visual element does not support text input.");

    /// <summary>
    /// Sets platform keyboard focus to the concrete element.
    /// </summary>
    protected virtual void FocusCore() => throw new NotSupportedException("This visual element does not support keyboard focus.");

    /// <summary>
    /// Sends one physical keyboard gesture through the concrete platform implementation.
    /// </summary>
    protected virtual void SendKeyGestureCore(KeyGesture keyGesture) =>
        throw new NotSupportedException("This visual element does not support keyboard input.");

    /// <summary>
    /// Gets a bounded textual selection preview through the concrete platform implementation.
    /// </summary>
    protected virtual string? GetSelectedTextCore(int maxCharacters) => null;

    /// <summary>
    /// Captures the concrete platform element.
    /// </summary>
    protected abstract Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates or retains the destination Context's canonical element over the same native identity.
    /// </summary>
    protected virtual VisualElement AdoptCore(VisualElementRetention destinationRetention) =>
        throw new NotSupportedException($"The visual element type '{GetType().Name}' does not support cross-Context adoption.");

    /// <summary>
    /// Attempts to convert an escaped platform exception into the public Automation exception contract.
    /// </summary>
    protected virtual bool TryConvertPlatformException(Exception exception, [NotNullWhen(true)] out Exception? convertedException)
    {
        convertedException = null;
        return false;
    }

    /// <summary>
    /// Releases only the platform resources owned by this canonical element.
    /// </summary>
    protected abstract void ReleaseCore();

    internal void ReleaseRetained()
    {
        if (_isReleased)
        {
            return;
        }

        _isReleased = true;
        ReleaseCore();
    }

    internal void ReleaseUnretained()
    {
        if (_isReleased)
        {
            return;
        }

        if (Identity.RetainerCount == 0)
        {
            Identity.RemoveFromMap(this);
        }

        _isReleased = true;
        ReleaseCore();
    }

    private void EnsureUsable()
    {
        Context.ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(_isReleased, this);
        GC.KeepAlive(Identity);
    }

    private void ExecuteAction<TState>(TState state, Action<VisualElement, TState> action)
    {
        EnsureUsable();
        try
        {
            action(this, state);
        }
        catch (Exception exception) when (TryConvertPlatformException(exception, out var convertedException))
        {
            if (ReferenceEquals(exception, convertedException))
            {
                throw;
            }

            throw convertedException;
        }
    }

    private sealed class RelationEnumerator : IVisualElementEnumerator
    {
        public VisualElementEnumerationResult Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                return _hasCurrent ? _current : throw new InvalidOperationException("The Enumerator has no current item.");
            }
        }

        object IEnumerator.Current => Current;

        public int Count { get; private set; } = -1;

        public int Index { get; private set; } = -1;

        private VisualElement? _origin;
        private readonly VisualElementRetention _retention;
        private readonly VisualElementRelation _relation;
        private readonly VisualElementQueryRequest _request;
        private IVisualElementCursor? _cursor;
        private VisualElementEnumerationResult _current;
        private bool _hasCurrent;
        private bool _isCompleted;
        private bool _isDisposed;

        public RelationEnumerator(VisualElement origin, VisualElementRelation relation, VisualElementQueryRequest request)
        {
            _origin = origin;
            _relation = relation;
            _request = request;
            _retention = origin.Context.CreateRetention();
            _retention.Retain(origin);
        }

        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _hasCurrent = false;
            Index = -1;
            if (_isCompleted)
            {
                return false;
            }

            try
            {
                var origin = _origin ?? throw new ObjectDisposedException(nameof(RelationEnumerator));
                if (_cursor is null)
                {
                    _cursor = origin.CreateEnumeratorCore(_relation, _request);
                    Count = _cursor.Count;
                }

                var hasNext = _cursor.MoveNext();
                Count = _cursor.Count;
                if (!hasNext)
                {
                    Complete();
                    return false;
                }

                _current = new VisualElementEnumerationResult(_cursor.Current);
                _hasCurrent = true;
                Index = _cursor.Index;
                return true;
            }
            catch (Exception exception) when (TryCreateFailure(exception, out var failure))
            {
                _current = new VisualElementEnumerationResult(failure);
                _hasCurrent = true;
                Complete();
                return true;
            }
        }

        public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _hasCurrent = false;
            Complete();
        }

        private bool TryCreateFailure(Exception exception, [NotNullWhen(true)] out VisualElementQueryFailure? failure)
        {
            var normalizedException = exception;
            if (_origin?.TryConvertPlatformException(exception, out var convertedException) == true)
            {
                normalizedException = convertedException;
            }

            return VisualElementFailure.TryCreate(normalizedException, out failure);
        }

        private void Complete()
        {
            if (_isCompleted)
            {
                return;
            }

            _isCompleted = true;
            _origin = null;
            DisposeHelper.DisposeToDefault(ref _cursor);
            _retention.Dispose();
        }
    }

    private readonly struct Void;
}