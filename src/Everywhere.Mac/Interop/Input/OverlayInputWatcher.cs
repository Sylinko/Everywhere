using Avalonia;
using Everywhere.Interop;
using Everywhere.Utilities;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Observes global overlay input with a listen-only event tap, including events directed to our own windows.
/// The tap and its run-loop source exist only while at least one overlay watch is active.
/// </summary>
public sealed class OverlayInputWatcher : OverlayInputWatcherBase
{
    public override bool IsSupported => true;

    private CGEventListener? _listener;

    /// <inheritdoc />
    protected override void StartWatching()
    {
        const CGEventMask mask = CGEventMask.MouseMoved |
            CGEventMask.LeftMouseDragged | CGEventMask.RightMouseDragged | CGEventMask.OtherMouseDragged |
            CGEventMask.LeftMouseDown | CGEventMask.RightMouseDown | CGEventMask.OtherMouseDown |
            CGEventMask.ScrollWheel | CGEventMask.KeyDown;

        _listener = new CGEventListener(CGEventTapOptions.ListenOnly, mask);
        _listener.EventReceived += HandleEvent;
        using var pointerEvent = new CGEvent((CGEventSource?)null);
        PublishPointer(pointerEvent.Location);
    }

    /// <inheritdoc />
    protected override void StopWatching()
    {
        if (_listener is { } listener) listener.EventReceived -= HandleEvent;
        DisposeHelper.DisposeToDefault(ref _listener);
    }

    private void HandleEvent(CGEventType type, CGEvent input, ref nint eventRef)
    {
        if (type == CGEventType.KeyDown)
        {
            // Copy fallback runs in Automation Host, which has a different PID from Main.
            if (input.GetLongValueField(CGEventField.EventSourceUserData) == CGInterop.SelectionCopyEventTag) return;

            var key = (CGKeyCode)input.GetLongValueField(CGEventField.KeyboardEventKeycode);
            var isMenuNavigation = key is CGKeyCode.Up or CGKeyCode.Down or CGKeyCode.Left or CGKeyCode.Right or
                CGKeyCode.Home or CGKeyCode.End or CGKeyCode.PageUp or CGKeyCode.PageDown or
                CGKeyCode.Enter or CGKeyCode.NumPadEnter or CGKeyCode.Space or CGKeyCode.Escape or CGKeyCode.Tab;
            HandleKeyPressed(isMenuNavigation);
            return;
        }

        var point = PublishPointer(input.Location);

        switch (type)
        {
            case CGEventType.LeftMouseDown:
            case CGEventType.RightMouseDown:
            case CGEventType.OtherMouseDown:
                HandlePointerPressed(point);
                break;
            case CGEventType.ScrollWheel:
                HandlePointerScrolled(point);
                break;
        }
    }

    private PixelPoint PublishPointer(CGPoint location)
    {
        // Quartz and Avalonia's macOS desktop coordinates both use top-left-origin desktop points,
        // independently of Retina backing scale. Do not multiply these positions by RenderScaling.
        var point = new PixelPoint((int)Math.Round(location.X), (int)Math.Round(location.Y));
        UpdatePointerPosition(point);
        return point;
    }
}
