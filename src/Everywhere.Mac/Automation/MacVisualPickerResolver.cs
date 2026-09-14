using Avalonia;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.Mac.Interop;
using Everywhere.ProcessIsolation;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Roles;
using ZLinq;

namespace Everywhere.Mac.Automation;

/// <summary>Combines Core Graphics window stacking with per-process Accessibility acquisition for Host-owned picker sessions.</summary>
[InHostProcess(ProcessRole.Automation)]
public sealed class MacVisualPickerResolver : IVisualPickerResolver
{
    /// <inheritdoc />
    public VisualElementQueryResult? Resolve(
        IVisualElementBackend backend,
        VisualElementRetention retention,
        PixelPoint point,
        ScreenSelectionMode mode,
        int excludedProcessId,
        VisualElementQueryRequest request)
    {
        if (backend is not MacVisualElementBackend macBackend)
        {
            throw new InvalidOperationException("The macOS visual picker requires a MacVisualElementBackend.");
        }

        if (mode == ScreenSelectionMode.Screen)
        {
            return macBackend.Query(retention, VisualElementLocator.FromPoint(point), VisualElementResolution.Screen, request);
        }

        var resolution = mode switch
        {
            ScreenSelectionMode.Window => VisualElementResolution.TopLevel,
            ScreenSelectionMode.Element => VisualElementResolution.Direct,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The visual picker does not support free-form selection."),
        };
        var observedProcessIds = new HashSet<int>();
        return CGWindowZOrder.Capture(CGDisplayTopology.Current).Windows
            .AsValueEnumerable()
            .Where(window => window.OwnerProcessId != excludedProcessId && window.Bounds.Contains(point) && observedProcessIds.Add(window.OwnerProcessId))
            .Select(window => macBackend.QueryElementAtPointForProcess(retention, window.OwnerProcessId, point, resolution, request)).OfType<VisualElementQueryResult>()
            .FirstOrDefault();
    }
}