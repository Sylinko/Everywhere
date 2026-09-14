using Everywhere.Automation;
using Everywhere.Interop;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Resolves interactive picker points inside the Automation Host without exposing native elements to Main.</summary>
public interface IVisualPickerResolver
{
    /// <summary>Resolves one current picker candidate into the supplied Context retention.</summary>
    VisualElementQueryResult? Resolve(
        IVisualElementBackend backend,
        VisualElementRetention retention,
        PixelPoint point,
        ScreenSelectionMode mode,
        int excludedProcessId,
        VisualElementQueryRequest request);
}

/// <summary>Maps picker modes to the standard point-based Backend acquisition contract.</summary>
/// <remarks>
/// Platform Hosts may replace this resolver when native window stacking is required to continue behind Main-owned picker windows.
/// </remarks>
public sealed class DefaultVisualPickerResolver : IVisualPickerResolver
{
    /// <summary>Gets the stateless shared resolver.</summary>
    public static DefaultVisualPickerResolver Shared { get; } = new();

    private DefaultVisualPickerResolver()
    {
    }

    /// <inheritdoc />
    public VisualElementQueryResult? Resolve(
        IVisualElementBackend backend,
        VisualElementRetention retention,
        PixelPoint point,
        ScreenSelectionMode mode,
        int excludedProcessId,
        VisualElementQueryRequest request)
    {
        var resolution = mode switch
        {
            ScreenSelectionMode.Screen => VisualElementResolution.Screen,
            ScreenSelectionMode.Window => VisualElementResolution.TopLevel,
            ScreenSelectionMode.Element => VisualElementResolution.Direct,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The visual picker does not support free-form selection."),
        };
        var result = backend.Query(retention, VisualElementLocator.FromPoint(point), resolution, request);
        return result?.Snapshot.ProcessId == excludedProcessId ? null : result;
    }
}