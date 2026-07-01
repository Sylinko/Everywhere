using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Interop;
using Serilog;
using ZLinq;

namespace Everywhere.Views;

public class VisualElementOverlayWindow : Window
{
    private WeakReference<IVisualElement>? _visualElement;

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

    public async void UpdateForVisualElement(IVisualElement? element)
    {
        if (element is null)
        {
            _visualElement = null;
            Hide();
        }
        else if (_visualElement?.TryGetTarget(out var existingElement) is not true || !Equals(existingElement, element))
        {
            if (_visualElement is null)
            {
                _visualElement = new WeakReference<IVisualElement>(element);
            }
            else
            {
                _visualElement.SetTarget(element);
            }

            PixelRect boundingRectangle;
            try
            {
                boundingRectangle = await Task.Run(() => element.BoundingRectangle).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                _visualElement = null;
                return;
            }
            catch (Exception ex)
            {
                _visualElement = null;
                Log.Logger.ForContext<VisualElementOverlayWindow>().Error(ex, "Failed to update OverlayWindow for visual element.");
                Hide();
                return;
            }

            if (boundingRectangle.Width <= 0 || boundingRectangle.Height <= 0)
            {
                _visualElement = null;
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
                _visualElement = null;
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
}