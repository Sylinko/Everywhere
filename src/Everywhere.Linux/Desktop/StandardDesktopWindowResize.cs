using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Allows a client-decorated window to request native window resizing.
/// </summary>
internal static class StandardDesktopWindowResize
{
    private const double GripThickness = 8;
    private static readonly Cursor HorizontalCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor NorthWestCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor NorthEastCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor SouthWestCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor SouthEastCursor = new(StandardCursorType.BottomRightCorner);

    /// <summary>
    /// Attaches edge gestures to a window that owns its client decorations.
    /// </summary>
    public static void Configure(Window window)
    {
        // The compositor also resizes tiled windows without a pointer gesture.
        // Keep their initial size usable, then accept subsequent configure events.
        window.SizeToContent = SizeToContent.Manual;
        if (!double.IsFinite(window.Width))
        {
            window.Width = window.MinWidth;
        }

        if (!double.IsFinite(window.Height))
        {
            window.Height = window.MinHeight;
        }

        window.Resized -= HandleResized;
        window.Resized += HandleResized;
        window.RemoveHandler(InputElement.PointerPressedEvent, HandlePointerPressed);
        window.RemoveHandler(InputElement.PointerMovedEvent, HandlePointerMoved);
        window.AddHandler(InputElement.PointerPressedEvent, HandlePointerPressed, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerMovedEvent, HandlePointerMoved, RoutingStrategies.Tunnel);
    }

    private static void HandleResized(object? sender, WindowResizedEventArgs args)
    {
        if (sender is Window window && args.Reason == WindowResizeReason.Layout)
        {
            // Chat restores automatic sizing at its minimum size. A later native
            // configure must be manual again before Chat's OnResized latch runs;
            // Window raises this event from its base OnResized implementation.
            window.SizeToContent = SizeToContent.Manual;
        }
    }

    private static void HandlePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not Window { CanResize: true, WindowState: WindowState.Normal } window ||
            !args.GetCurrentPoint(window).Properties.IsLeftButtonPressed ||
            GetResizeEdge(window.ClientSize, args.GetPosition(window)) is not { } edge)
        {
            return;
        }

        // Automatic content sizing would otherwise undo compositor resize events.
        window.SizeToContent = SizeToContent.Manual;
        args.Handled = true;
        window.BeginResizeDrag(edge, args);
    }

    private static void HandlePointerMoved(object? sender, PointerEventArgs args)
    {
        if (sender is not Window window)
        {
            return;
        }

        var edge = window is { CanResize: true, WindowState: WindowState.Normal }
            ? GetResizeEdge(window.ClientSize, args.GetPosition(window))
            : null;
        window.Cursor = edge switch
        {
            WindowEdge.West or WindowEdge.East => HorizontalCursor,
            WindowEdge.North or WindowEdge.South => VerticalCursor,
            WindowEdge.NorthWest => NorthWestCursor,
            WindowEdge.NorthEast => NorthEastCursor,
            WindowEdge.SouthWest => SouthWestCursor,
            WindowEdge.SouthEast => SouthEastCursor,
            _ => null,
        };
    }

    private static WindowEdge? GetResizeEdge(Size size, Point position)
    {
        if (position.X < 0 || position.Y < 0 || position.X > size.Width || position.Y > size.Height)
        {
            return null;
        }

        var isLeft = position.X <= GripThickness;
        var isRight = position.X >= size.Width - GripThickness;
        var isTop = position.Y <= GripThickness;
        var isBottom = position.Y >= size.Height - GripThickness;
        return (isLeft, isRight, isTop, isBottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }
}