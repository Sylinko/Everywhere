using Everywhere.Automation;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Creates one platform-native text-selection monitor for an Automation Host connection.</summary>
public interface ITextSelectionMonitorFactory
{
    /// <summary>Starts one monitor whose finite visual operations run through the supplied Context boundary.</summary>
    ITextSelectionMonitor Create(
        ITextSelectionMonitorContext context,
        int mainProcessId,
        TextSelectionMonitoringConfiguration configuration,
        Func<TextSelectionObservation, CancellationToken, ValueTask> publish);
}

/// <summary>Runs finite platform selection reads through one hosted visual Context.</summary>
public interface ITextSelectionMonitorContext
{
    /// <summary>Serializes one synchronous native observation with the Context's other operations.</summary>
    ValueTask<TResult> ExecuteAsync<TResult>(
        Func<VisualContext, IVisualElementBackend, CancellationToken, TResult> operation,
        CancellationToken cancellationToken = default);

    /// <summary>Releases one untransferred retained source through the Context operation queue.</summary>
    ValueTask ReleaseAsync(TextSelectionSource source);
}

/// <summary>Owns one active native text-selection monitor.</summary>
public interface ITextSelectionMonitor : IAsyncDisposable
{
    /// <summary>Atomically replaces the policy used for subsequent detection decisions.</summary>
    ValueTask UpdateConfigurationAsync(TextSelectionMonitoringConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>Owns a retained source element observed together with selected text.</summary>
public sealed class TextSelectionSource
{
    /// <summary>Gets the retained source observation.</summary>
    public VisualElementQueryResult Result { get; }

    private VisualElementRetention? _retention;

    /// <summary>Creates an owned source from one Context retention and its result.</summary>
    public TextSelectionSource(VisualElementRetention retention, VisualElementQueryResult result)
    {
        if (!ReferenceEquals(retention.Context, result.Element.Context))
        {
            throw new ArgumentException("The text-selection source must belong to its retention Context.", nameof(result));
        }

        _retention = retention;
        Result = result;
    }

    internal VisualElementRetention TakeRetention() =>
        Interlocked.Exchange(ref _retention, null) ?? throw new ObjectDisposedException(nameof(TextSelectionSource));

    internal void ReleaseWithinContext() => Interlocked.Exchange(ref _retention, null)?.Dispose();
}

/// <summary>Owns one platform observation until Automation RPC accepts or discards it.</summary>
public sealed class TextSelectionObservation : IAsyncDisposable
{
    /// <summary>Gets the nonempty selected text.</summary>
    public string Text { get; }

    /// <summary>Gets whether native or transport limits may have omitted trailing text.</summary>
    public bool IsTextIncomplete { get; }

    /// <summary>Gets the optional retained source observed with the text.</summary>
    public TextSelectionSource? Source { get; private set; }

    private readonly ITextSelectionMonitorContext _context;

    /// <summary>Creates one owned observation.</summary>
    public TextSelectionObservation(
        ITextSelectionMonitorContext context,
        string text,
        TextSelectionSource? source = null,
        bool isTextIncomplete = false)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("A text-selection observation must contain text.", nameof(text));

        _context = context;
        Text = text;
        Source = source;
        IsTextIncomplete = isTextIncomplete;
    }

    internal TextSelectionSource? TakeSource()
    {
        var source = Source;
        Source = null;
        return source;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Source is { } source) await _context.ReleaseAsync(source).ConfigureAwait(false);
        Source = null;
    }
}

internal sealed class UnsupportedTextSelectionMonitorFactory : ITextSelectionMonitorFactory
{
    public static UnsupportedTextSelectionMonitorFactory Shared { get; } = new();

    public ITextSelectionMonitor Create(
        ITextSelectionMonitorContext context,
        int mainProcessId,
        TextSelectionMonitoringConfiguration configuration,
        Func<TextSelectionObservation, CancellationToken, ValueTask> publish) =>
        throw new NotSupportedException("Text-selection monitoring is unavailable on this platform.");
}