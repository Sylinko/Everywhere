using Avalonia;
using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

public class NSScreenVisualElement(NSScreen screen) : IVisualElement
{
    private readonly NSScreen _screen = screen;

    public string Id => $"Screen:{GetScreenNumber(_screen)}";

    public IVisualElement? Parent => null;

    public VisualElementSiblingAccessor SiblingAccessor => new ScreenSiblingAccessor(this);

    public IEnumerable<IVisualElement> Children
    {
        get
        {
            var bounds = BoundingRectangle;
            var apps = NSWorkspace.SharedWorkspace.RunningApplications;

            foreach (var app in apps)
            {
                if (app.ActivationPolicy == NSApplicationActivationPolicy.Prohibited) continue;

                if (AXUIElement.ElementFromPid(app.ProcessIdentifier) is not { } axApp) continue;

                foreach (var child in axApp.Children)
                {
                    // Filter for windows (TopLevel) that are on this screen
                    if (child.Type == VisualElementType.TopLevel &&
                        child.BoundingRectangle.Intersects(bounds))
                    {
                        yield return child;
                    }
                }
            }
        }
    }

    public VisualElementType Type => VisualElementType.Screen;

    public VisualElementStates States => VisualElementStates.None;

    public string Name => _screen.LocalizedName;

    public PixelRect BoundingRectangle
    {
        get
        {
            var frame = _screen.Frame;
            // NSScreen.Screens[0] is the primary screen.
            // Cocoa coordinates: (0,0) is bottom-left of primary screen.
            // Quartz/Avalonia coordinates: (0,0) is top-left of primary screen.

            var primaryFrame = NSScreen.Screens[0].Frame;
            var x = (int)frame.X;
            var y = (int)(primaryFrame.Height - (frame.Y + frame.Height));

            return new PixelRect(x, y, (int)frame.Width, (int)frame.Height);
        }
    }

    public int ProcessId => 0;

    public nint NativeWindowHandle => 0;

    public string? GetText(int maxLength = -1) => null;

    public void Invoke() => throw new InvalidOperationException();

    public void SetText(string text) => throw new InvalidOperationException();

    public void SendShortcut(KeyboardShortcut shortcut) => throw new InvalidOperationException();

    public string? GetSelectionText() => null;

    public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
    {
        var bounds = BoundingRectangle;
        var rect = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

#pragma warning disable CA1422
        using var cgImage = CGImage.ScreenImage(0, rect);
#pragma warning restore CA1422

        if (cgImage is null)
        {
            return Task.FromException<IVisualElement.ICapturedBitmapData>(new InvalidOperationException("Failed to create CGImage wrapper."));
        }

        return Task.FromResult<IVisualElement.ICapturedBitmapData>(new CapturedBitmapData(cgImage, 1d));
    }

    private static int GetScreenNumber(NSScreen screen)
    {
        return (screen.DeviceDescription["NSScreenNumber"] as NSNumber)?.Int32Value ?? 0;
    }

    private sealed class ScreenSiblingAccessor(NSScreenVisualElement element) : VisualElementSiblingAccessor
    {
        private NSScreen[]? _screens;
        private int _index;

        protected override void EnsureResources()
        {
            if (_screens != null) return;
            _screens = NSScreen.Screens;
            _index = Array.IndexOf(_screens, element._screen);
        }

        protected override void ReleaseResources()
        {
            _screens = null;
        }

        protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
        {
            if (_screens is not { } screens || _index < 0) yield break;

            for (var i = _index + 1; i < screens.Length; i++)
            {
                yield return new NSScreenVisualElement(screens[i]);
            }
        }

        protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
        {
            if (_screens is not { } screens || _index < 0) yield break;

            for (var i = _index - 1; i >= 0; i--)
            {
                yield return new NSScreenVisualElement(screens[i]);
            }
        }
    }
}