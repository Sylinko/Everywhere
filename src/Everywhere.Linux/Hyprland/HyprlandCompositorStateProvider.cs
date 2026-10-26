using Avalonia;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Hyprland;

/// <summary>
/// Translates Hyprland IPC observations into the common Linux desktop model.
/// </summary>
public sealed class HyprlandCompositorStateProvider(HyprlandConnection connection) : ICompositorStateProvider
{
    /// <inheritdoc />
    public string InstanceId => connection.Instance;

    /// <inheritdoc />
    public WindowStackingOrderKind StackingOrderKind => WindowStackingOrderKind.FocusHistoryEstimate;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutputInfo>> GetOutputsAsync(CancellationToken cancellationToken = default)
    {
        var monitors = await connection.ReadAsync<HyprlandMonitor[]>("monitors", cancellationToken).ConfigureAwait(false);
        return VisibleOutputs(monitors).Select(monitor => new OutputInfo(monitor.Name, monitor.Bounds, monitor.Scale)).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<PixelPoint> GetGlobalCursorPositionAsync(CancellationToken cancellationToken = default)
    {
        var pointer = await connection.ReadAsync<Dictionary<string, int>>("cursorpos", cancellationToken).ConfigureAwait(false);
        return new PixelPoint(pointer["x"], pointer["y"]);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CompositorWindowInfo>> GetWindowsInStackingOrderAsync(CancellationToken cancellationToken = default)
    {
        var monitors = VisibleOutputs(await connection.ReadAsync<HyprlandMonitor[]>("monitors", cancellationToken).ConfigureAwait(false));
        var windows = await connection.ReadAsync<HyprlandWindow[]>("clients", cancellationToken).ConfigureAwait(false);
        // IPC does not expose the render stack. Keep the existing hit-test estimate
        // explicit; focus history is not a substitute for authoritative Z-order.
        var visibleWindows = from window in windows
                             join output in monitors on window.MonitorId equals output.Id
                             where IsVisible(window, output)
                             orderby output.SpecialWorkspace.Id != 0 && window.Workspace.Id == output.SpecialWorkspace.Id descending,
                                 window.IsFloating descending,
                                 window.FocusHistoryId
                             select ToWindowInfo(window, output.Name);
        return visibleWindows.ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<CompositorWindowInfo?> GetActiveWindowAsync(CancellationToken cancellationToken = default)
    {
        var window = await connection.ReadAsync<HyprlandWindow>("activewindow", cancellationToken).ConfigureAwait(false);
        if (!window.IsMapped || window.IsHidden || window.ProcessId <= 0 || string.IsNullOrEmpty(window.Address))
        {
            return null;
        }

        var monitors = await connection.ReadAsync<HyprlandMonitor[]>("monitors", cancellationToken).ConfigureAwait(false);
        var output = VisibleOutputs(monitors).FirstOrDefault(monitor => monitor.Id == window.MonitorId);
        return output is not null && IsVisible(window, output) ? ToWindowInfo(window, output.Name) : null;
    }

    /// <summary>
    /// Filters mirror sinks and disabled outputs before constructing desktop coordinates.
    /// </summary>
    public static HyprlandMonitor[] VisibleOutputs(IEnumerable<HyprlandMonitor> outputs)
    {
        return outputs.Where(item => !item.IsDisabled && item.MirrorOf == "none").OrderBy(item => item.X).ThenBy(item => item.Y).ToArray();
    }

    private static bool IsVisible(HyprlandWindow window, HyprlandMonitor output)
    {
        return window.IsMapped && !window.IsHidden
            && (window.Workspace.Id == output.ActiveWorkspace.Id
                || output.SpecialWorkspace.Id != 0 && window.Workspace.Id == output.SpecialWorkspace.Id);
    }

    private static CompositorWindowInfo ToWindowInfo(HyprlandWindow window, string outputName)
    {
        // 'at' and 'size' anchor the application surface. IPC does not provide a
        // separate server-decoration rectangle; do not manufacture one here.
        return new(
            window.Address,
            window.Handle,
            window.ProcessId,
            window.ApplicationId,
            window.Title,
            outputName,
            window.Bounds,
            null,
            window.Fullscreen != 0);
    }
}