using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Everywhere.Interop;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Base window class for screen selection windows.
/// </summary>
public abstract class ScreenSelectionWindow : Window
{
    protected ScreenSelectionWindow()
    {
        Topmost = true;
        CanResize = false;
        CanMaximize = false;
        CanMinimize = false;
        ShowInTaskbar = false;
        BorderThickness = new Thickness(0);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }
}

/// <summary>
/// Transparent window used for screen selection.
/// Provides methods to set placement based on screen bounds.
/// </summary>
public abstract class ScreenSelectionTransparentWindow : ScreenSelectionWindow
{
    protected ScreenSelectionTransparentWindow()
    {
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Cross);
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = SystemDecorations.None;
        SizeToContent = SizeToContent.Manual;
    }

    /// <summary>
    /// Sets the window placement based on the specified screen bounds.
    /// </summary>
    /// <param name="screenBounds"></param>
    /// <param name="scale"></param>
    protected void SetPlacement(PixelRect screenBounds, out double scale)
    {
        Position = screenBounds.Position;
        scale = DesktopScaling; // we must set Position first to get the correct scaling factor
        Width = screenBounds.Width / scale;
        Height = screenBounds.Height / scale;
    }
}

public abstract class ScreenSelectionSessionWindowBase(ScreenSelectionModes allowedModes, ScreenSelectionModes initialMode)
    : ScreenSelectionTransparentWindow
{
    protected ScreenSelectionModes CurrentMode { get; private set; } = initialMode;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        OnMouseWheel((int)e.Delta.Y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                break;
            case Key.NumPad1 or Key.D1 or Key.F1:
                SetMode(ScreenSelectionModes.Screen);
                break;
            case Key.NumPad2 or Key.D2 or Key.F2:
                SetMode(ScreenSelectionModes.Window);
                break;
            case Key.NumPad3 or Key.D3 or Key.F3:
                SetMode(ScreenSelectionModes.Element);
                break;
            case Key.NumPad4 or Key.D4 or Key.F4:
                SetMode(ScreenSelectionModes.Free);
                break;
        }
    }

    private void OnMouseWheel(int delta)
    {
        var newMode = delta < 0 ? (int)CurrentMode << 1 : (int)CurrentMode >> 1;
        if (!allowedModes.HasFlag((ScreenSelectionModes)newMode) || newMode <= 0) return;

        CurrentMode = (ScreenSelectionModes)newMode;
        HandleModeChanged();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.Properties.IsLeftButtonPressed)
        {
            OnLeftButtonDown();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            if (OnLeftButtonUp())
            {
                Close();
            }
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            Cancel();
        }
    }

    /// <summary>
    /// Called when Left Button Down.
    /// </summary>
    protected virtual void OnLeftButtonDown() { }

    /// <summary>
    /// Called when Left Button Up.
    /// Returns true if the picker should close.
    /// </summary>
    protected virtual bool OnLeftButtonUp() => true;

    protected void SetMode(ScreenSelectionModes mode)
    {
        if (!allowedModes.HasFlag(mode)) return;
        CurrentMode = mode;
        HandleModeChanged();
    }

    protected virtual void HandleModeChanged() { }

    protected void Cancel()
    {
        OnCanceled();
        Close();
    }

    protected virtual void OnCanceled() { }
}

/// <summary>
/// Mask window that displays the overlay during screen selection.
/// </summary>
public sealed class ScreenSelectionMaskWindow : ScreenSelectionTransparentWindow
{
    private readonly Border _maskBorder;
    private readonly Border _elementBoundsBorder;
    private readonly Panel _highlightsPanel;
    private readonly PixelRect _screenBounds;
    private readonly double _scale;

    public ScreenSelectionMaskWindow(PixelRect screenBounds)
    {
        Content = new Panel
        {
            IsHitTestVisible = false,
            Children =
            {
                (_maskBorder = new Border
                {
                    Background = Brushes.Black,
                    Opacity = 0.0
                }),
                (_highlightsPanel = new Panel()),
                (_elementBoundsBorder = new Border
                {
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                })
            }
        };

        _screenBounds = screenBounds;
        SetPlacement(screenBounds, out _scale);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _maskBorder.Animate(OpacityProperty).To(0.4).WithDuration(TimeSpan.FromMilliseconds(200)).Start();
    }

    public void SetImage(Bitmap? bitmap)
    {
        // We use an ImageBrush here instead of an Image control
        // to avoid issues with scaling and rearrangement.
        Background = new ImageBrush(bitmap);
    }

    public void SetMask(PixelRect rect)
    {
        var maskRect = rect.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
        if (maskRect.Width < 0 || maskRect.Height < 0)
        {
            // Sometimes the rect can be invalid due to DPI scaling and rounding, so we need to handle that case.
            maskRect = default;
        }

        _maskBorder.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(Bounds), new RectangleGeometry(maskRect));
        _elementBoundsBorder.Margin = new Thickness(maskRect.X, maskRect.Y, 0, 0);
        _elementBoundsBorder.Width = maskRect.Width;
        _elementBoundsBorder.Height = maskRect.Height;
    }

    public void SetHighlights(IReadOnlyList<PixelRect> rects)
    {
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];

            Border highlightBorder;
            if (_highlightsPanel.Children.Count < i + 1)
            {
                _highlightsPanel.Children.Add(highlightBorder = new Border
                {
                    BorderBrush = Brushes.DodgerBlue,
                    BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Colors.DodgerBlue, 0.6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Opacity = 0.3
                });
            }
            else
            {
                highlightBorder = (Border)_highlightsPanel.Children[i];
            }

            var highlightRect = rect.Translate(-(PixelVector)_screenBounds.Position).ToRect(_scale);
            if (highlightRect.Width < 0 || highlightRect.Height < 0)
            {
                // Sometimes the rect can be invalid due to DPI scaling and rounding, so we need to handle that case.
                highlightRect = default;
            }

            highlightBorder.Margin = new Thickness(highlightRect.X, highlightRect.Y, 0, 0);
            highlightBorder.Width = highlightRect.Width;
            highlightBorder.Height = highlightRect.Height;
        }

        for (var i = _highlightsPanel.Children.Count - 1; i >= rects.Count; i--)
        {
            _highlightsPanel.Children.RemoveAt(i);
        }
    }
}

public sealed class ScreenSelectionToolTipWindow : ScreenSelectionWindow
{
    public ScreenSelectionToolTip ToolTip { get; }

    public ScreenSelectionToolTipWindow(ScreenSelectionModes allowedModes, ScreenSelectionModes initialMode)
    {
        Content = ToolTip = new ScreenSelectionToolTip(allowedModes)
        {
            CurrentMode = initialMode
        };
        SizeToContent = SizeToContent.WidthAndHeight;
        SystemDecorations = SystemDecorations.BorderOnly;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaToDecorationsHint = true;
    }
}