using Avalonia.Controls;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Serilog;
using ZLinq;

namespace Everywhere.Views;

public class VisualElementOverlayWindow : Window
{
    private WeakReference<VisualElementAttachment>? _visualAttachment;
    private WeakReference<DebuggerVisualContext>? _visualContext;
    private WeakReference<VisualElement>? _visualElement;
    private int? _visualTargetId;
    private int _updateVersion;

    public VisualElementOverlayWindow()
    {
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        IsHitTestVisible = false;
        Background = null;
        Focusable = false;
        Topmost = true;

        var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
        windowHelper.SetFocusable(this, false);
        windowHelper.SetHitTestVisible(this, false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>Queries and displays one process-local visual element.</summary>
    public async void UpdateForLocalVisualElement(VisualElement? element)
    {
        var version = ++_updateVersion;
        _visualAttachment = null;
        _visualContext = null;
        _visualTargetId = null;
        if (element is null)
        {
            _visualElement = null;
            Hide();
            return;
        }

        if (_visualElement?.TryGetTarget(out var existingElement) is true && Equals(existingElement, element)) return;
        if (_visualElement is null) _visualElement = new WeakReference<VisualElement>(element);
        else _visualElement.SetTarget(element);

        try
        {
            var snapshot = await Task.Run(() => element.Query(new VisualElementQueryRequest(VisualElementFields.Bounds, 0)).Snapshot)
                .WaitAsync(TimeSpan.FromSeconds(1));
            if (version != _updateVersion) return;
            UpdateForBounds(snapshot.Bounds);
        }
        catch (TimeoutException)
        {
            if (version == _updateVersion) _visualElement = null;
        }
        catch (Exception ex)
        {
            if (version != _updateVersion) return;
            _visualElement = null;
            Log.Logger.ForContext<VisualElementOverlayWindow>().Error(ex, "Failed to update OverlayWindow for local visual element.");
            Hide();
        }
    }

    /// <summary>Queries and displays one Host-owned visual attachment.</summary>
    public async void UpdateForVisualElement(VisualElementAttachment? attachment)
    {
        if (attachment is null)
        {
            ++_updateVersion;
            _visualAttachment = null;
            _visualContext = null;
            _visualElement = null;
            _visualTargetId = null;
            Hide();
            return;
        }

        if (_visualAttachment?.TryGetTarget(out var existingAttachment) is true && ReferenceEquals(existingAttachment, attachment)) return;
        var version = ++_updateVersion;
        _visualContext = null;
        _visualElement = null;
        _visualTargetId = null;
        if (_visualAttachment is null) _visualAttachment = new WeakReference<VisualElementAttachment>(attachment);
        else _visualAttachment.SetTarget(attachment);

        try
        {
            var snapshot = await attachment.GetElementSnapshotAsync(VisualElementFields.Bounds);
            if (version != _updateVersion || _visualAttachment.TryGetTarget(out var currentAttachment) is not true ||
                !ReferenceEquals(currentAttachment, attachment)) return;
            UpdateForBounds(snapshot.Bounds);
        }
        catch (Exception ex)
        {
            if (version != _updateVersion) return;
            _visualAttachment = null;
            Log.Logger.ForContext<VisualElementOverlayWindow>().Error(ex, "Failed to update OverlayWindow for remote visual element.");
            Hide();
        }
    }

    /// <summary>Queries and displays one Host-owned published visual target.</summary>
    public async void UpdateForRemoteVisualTarget(DebuggerVisualContext? context, int? targetId)
    {
        if (context is null || targetId is null)
        {
            ++_updateVersion;
            _visualAttachment = null;
            _visualContext = null;
            _visualElement = null;
            _visualTargetId = null;
            Hide();
            return;
        }

        if (_visualTargetId == targetId && _visualContext?.TryGetTarget(out var existingContext) is true &&
            ReferenceEquals(existingContext, context)) return;
        var version = ++_updateVersion;
        _visualAttachment = null;
        _visualElement = null;
        _visualTargetId = targetId;
        if (_visualContext is null) _visualContext = new WeakReference<DebuggerVisualContext>(context);
        else _visualContext.SetTarget(context);

        try
        {
            var snapshot = await context.GetTargetSnapshotAsync(targetId.Value, VisualElementFields.Bounds);
            if (version != _updateVersion || _visualTargetId != targetId ||
                _visualContext.TryGetTarget(out var currentContext) is not true || !ReferenceEquals(currentContext, context)) return;
            UpdateForBounds(snapshot.Bounds);
        }
        catch (Exception ex)
        {
            if (version != _updateVersion) return;
            _visualContext = null;
            _visualTargetId = null;
            Log.Logger.ForContext<VisualElementOverlayWindow>().Error(ex, "Failed to update OverlayWindow for remote visual target.");
            Hide();
        }
    }

    private void UpdateForBounds(PixelRect? bounds)
    {
        if (bounds is not { Width: > 0, Height: > 0 } boundingRectangle)
        {
            _visualAttachment = null;
            _visualContext = null;
            _visualElement = null;
            _visualTargetId = null;
            Hide();
            return;
        }

        var screenBounds = Screens.All
            .AsValueEnumerable()
            .Select(s => s.Bounds)
            .Aggregate((a, b) => a.Union(b));

        // Clamp to screen bounds
        var x = Math.Clamp(boundingRectangle.X, screenBounds.X, screenBounds.Right);
        var y = Math.Clamp(boundingRectangle.Y, screenBounds.Y, screenBounds.Bottom);
        var right = Math.Min(boundingRectangle.Right, screenBounds.Right);
        var bottom = Math.Min(boundingRectangle.Bottom, screenBounds.Bottom);
        var width = right - x;
        var height = bottom - y;

        if (width <= 0 || height <= 0)
        {
            _visualAttachment = null;
            _visualContext = null;
            _visualElement = null;
            _visualTargetId = null;
            Hide();
            return;
        }

        Position = new PixelPoint(x, y);

        var scaling = DesktopScaling;
        Width = width / scaling;
        Height = height / scaling;

        Show();
    }
}
