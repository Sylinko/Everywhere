using System.Text;

namespace Everywhere.Chat;

/// <summary>
/// Stores streamed text once, without a dispatcher-owned mirror. Changes notify on the writer's
/// thread; consumers capture text and version together before parsing or serializing.
/// </summary>
/// <remarks>
/// The chat execution domain serializes writes. The lock only coordinates those writes with UI
/// and background snapshot readers; callers must not rely on it to order concurrent notifications.
/// Complete initial text stays a string until another append needs mutable storage.
/// </remarks>
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
            return _builder?.Length ?? _snapshot?.Length ?? 0;
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
    private StringBuilder? _builder;
    // Null only after the builder changes and before the next full snapshot is requested.
    private string? _snapshot = string.Empty;
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
            if (_builder is null && _snapshot is { Length: 0 })
            {
                // Construction and deserialization both supply their complete text in one append.
                _snapshot = value;
            }
            else
            {
                _builder ??= new StringBuilder(_snapshot, (_snapshot?.Length ?? 0) + value.Length);
                _builder.Append(value);
                _snapshot = null;
            }

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
            _builder?.Clear();
            _snapshot = string.Empty;
            _version++;
            _epoch++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return this;
    }

    /// <summary>
    /// Captures one consistent value, reusing its string until the text changes.
    /// </summary>
    public ChatTextSnapshot CaptureSnapshot()
    {
        using var scope = _lock.EnterScope();
        _snapshot ??= _builder?.ToString() ?? string.Empty;
        return new ChatTextSnapshot(_snapshot, _version, _epoch);
    }

    /// <summary>
    /// Copies a bounded preview without allocating the full stream.
    /// </summary>
    public string GetPrefix(int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        using var scope = _lock.EnterScope();
        if (_snapshot is { } snapshot)
        {
            var length = Math.Min(maximumLength, snapshot.Length);
            if (length > 0 && length < snapshot.Length && char.IsHighSurrogate(snapshot[length - 1]))
            {
                length--;
            }

            return snapshot[..length];
        }

        if (_builder is not { } builder)
        {
            return string.Empty;
        }

        var prefixLength = Math.Min(maximumLength, builder.Length);
        if (prefixLength > 0 && prefixLength < builder.Length && char.IsHighSurrogate(builder[prefixLength - 1]))
        {
            prefixLength--;
        }

        return builder.ToString(0, prefixLength);
    }

    /// <inheritdoc />
    public override string ToString() => CaptureSnapshot().Text;
}

/// <summary>
/// A text value with its parse-cache revision and replacement epoch.
/// </summary>
public readonly record struct ChatTextSnapshot(string Text, long Version, long Epoch);