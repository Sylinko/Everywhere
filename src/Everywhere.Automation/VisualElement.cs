using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;

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
    /// Gets the Context that owns this element's logical identity domain.
    /// </summary>
    protected VisualContext Context { get; }

    internal VisualContext OwnerContext => Context;

    internal bool HasIdentityEntry => _identityEntry is not null;

    internal VisualElementIdentityEntry IdentityEntry =>
        _identityEntry ?? throw new InvalidOperationException("The visual element has not entered its Context identity map.");

    private VisualElementIdentityEntry? _identityEntry;
    private bool _isReleased;

    /// <summary>
    /// Initializes an unretained platform candidate. The owning identity map attaches it atomically before exposure.
    /// </summary>
    protected VisualElement(VisualContext context, string id)
    {
        Context = context;
        Id = id;
    }

    /// <summary>
    /// Queries bounded scalar fields through this element's concrete platform implementation.
    /// </summary>
    public virtual VisualElementQueryResult Query(VisualElementQueryRequest request)
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
    /// Creates a lazy relation Enumerator through this element's concrete platform implementation.
    /// </summary>
    public virtual IVisualElementEnumerator CreateEnumerator(VisualElementRelation relation, VisualElementEnumerationOptions options)
    {
        EnsureUsable();
        try
        {
            return CreateEnumeratorCore(relation, options);
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
    /// Invokes this element's semantic default action.
    /// </summary>
    public virtual void Invoke() => ExecuteAction(default(Void), static (element, _) => element.InvokeCore());

    /// <summary>
    /// Replaces this element's editable scalar text.
    /// </summary>
    public virtual void SetText(string text) => ExecuteAction(text, static (element, value) => element.SetTextCore(value));

    /// <summary>
    /// Sets keyboard focus to this element.
    /// </summary>
    public virtual void Focus() => ExecuteAction(default(Void), static (element, _) => element.FocusCore());

    /// <summary>
    /// Sends one keyboard gesture to this element.
    /// </summary>
    public virtual void SendKeyGesture(KeyGesture keyGesture) =>
        ExecuteAction(keyGesture, static (element, value) => element.SendKeyGestureCore(value));

    /// <summary>
    /// Gets a bounded textual representation of this element's current selection.
    /// </summary>
    /// <param name="maxCharacters">The maximum number of UTF-16 characters to return.</param>
    /// <returns>The selected text or selected child labels, or <see langword="null" /> when no textual selection is available.</returns>
    public virtual string? GetSelectedText(int maxCharacters)
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
    public virtual async Task<IVisualElementCapture> CaptureAsync(CancellationToken cancellationToken = default)
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
    /// Queries bounded scalar fields through the concrete platform implementation.
    /// </summary>
    protected abstract VisualElementQueryResult QueryCore(VisualElementQueryRequest request);

    /// <summary>
    /// Creates a lazy relation Enumerator through the concrete platform implementation.
    /// </summary>
    protected abstract IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementEnumerationOptions options);

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

    internal bool IsOwnedBy(VisualContext context) => ReferenceEquals(Context, context);

    internal void AttachIdentity(VisualElementIdentityEntry entry)
    {
        if (_identityEntry is not null || _isReleased)
        {
            throw new InvalidOperationException("The visual element cannot enter an identity map more than once.");
        }

        _identityEntry = entry;
    }

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

        if (_identityEntry is { RetainerCount: 0 } entry)
        {
            entry.RemoveFromMap();
        }

        _isReleased = true;
        ReleaseCore();
    }

    private void EnsureUsable()
    {
        Context.ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(_isReleased, this);
        _ = IdentityEntry;
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

    private readonly struct Void;
}