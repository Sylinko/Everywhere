using Everywhere.Automation;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Interop;

/// <summary>
/// Owns one observed text selection and either its retained remote source or a legacy in-process locator.
/// </summary>
public sealed class TextSelectionData : IDisposable
{
    /// <summary>Gets the selected text, or <see langword="null" /> when no text is attached.</summary>
    public string? Text { get; }

    /// <summary>Gets whether native or transport limits may have omitted trailing text.</summary>
    public bool IsTextIncomplete { get; }

    /// <summary>Gets the legacy in-process locator used by platforms outside process isolation.</summary>
    public VisualElementLocator? Locator { get; }

    /// <summary>Gets the topological result resolved from <see cref="Locator" />.</summary>
    public VisualElementResolution Resolution { get; }

    private RemoteVisualAnchor? _source;
    private readonly Func<bool>? _isCurrent;

    internal bool IsCurrent => _isCurrent?.Invoke() ?? true;

    /// <summary>Creates a locator-based in-process observation.</summary>
    public TextSelectionData(
        string? text,
        VisualElementLocator? locator,
        VisualElementResolution resolution = VisualElementResolution.Direct)
    {
        Text = text;
        Locator = locator;
        Resolution = resolution;
    }

    internal TextSelectionData(
        string text,
        bool isTextIncomplete,
        RemoteVisualAnchor? source,
        Func<bool> isCurrent)
    {
        Text = text;
        IsTextIncomplete = isTextIncomplete;
        _source = source;
        _isCurrent = isCurrent;
    }

    internal RemoteVisualAnchor? TakeSource() => Interlocked.Exchange(ref _source, null);

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _source, null)?.Dispose();
}