using System.Text;

namespace Everywhere.Chat;

/// <summary>
/// Stores streamed text once, without a dispatcher-owned mirror. Changes notify on the writer's
/// thread; consumers capture text and version together before parsing or serializing.
/// </summary>
public sealed class ChatTextBuffer
{
    /// <summary>
    /// Gets the current character count.
    /// </summary>
    public int Length
    {
        get
        {
            using var scope = _lock.EnterScope();
            return _text.Length;
        }
    }

    /// <summary>
    /// Gets the monotonically increasing text revision.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Gets the replacement epoch. A parse from before Clear must not be displayed afterwards.
    /// </summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>
    /// Signals a change after releasing the text lock.
    /// </summary>
    public event EventHandler? Changed;

    private readonly Lock _lock = new();
    private readonly StringBuilder _text = new();
    private long _version;
    private long _epoch;

    /// <summary>
    /// Appends text and invalidates its owner synchronously.
    /// </summary>
    public ChatTextBuffer Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return this;
        }

        using (_lock.EnterScope())
        {
            _text.Append(value);
            _version++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return this;
    }

    /// <summary>
    /// Replaces the text with an empty buffer and invalidates in-flight parses of its old content.
    /// </summary>
    public ChatTextBuffer Clear()
    {
        using (_lock.EnterScope())
        {
            _text.Clear();
            _version++;
            _epoch++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return this;
    }

    /// <summary>
    /// Copies one consistent value for background work.
    /// </summary>
    public ChatTextSnapshot CaptureSnapshot()
    {
        using var scope = _lock.EnterScope();
        return new ChatTextSnapshot(_text.ToString(), _version, _epoch);
    }

    /// <summary>
    /// Copies a bounded preview without allocating the full stream.
    /// </summary>
    public string GetPrefix(int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        using var scope = _lock.EnterScope();
        var length = Math.Min(maximumLength, _text.Length);
        if (length > 0 && length < _text.Length && char.IsHighSurrogate(_text[length - 1]))
        {
            length--;
        }

        return _text.ToString(0, length);
    }

    /// <inheritdoc />
    public override string ToString() => CaptureSnapshot().Text;
}

/// <summary>
/// A text value with its parse-cache revision and replacement epoch.
/// </summary>
public readonly record struct ChatTextSnapshot(string Text, long Version, long Epoch);