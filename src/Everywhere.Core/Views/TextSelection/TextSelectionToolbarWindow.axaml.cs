using Avalonia.Controls;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using TextSelectionToolbarAction = Everywhere.TextSelection.TextSelectionToolbarAction;

namespace Everywhere.Views;

/// <summary>
/// A small floating toolbar shown next to text the user has just selected in another application.
/// </summary>
public partial class TextSelectionToolbarWindow : Window
{
    /// <summary>
    /// Vertical gap between the anchor point and the toolbar, in physical pixels.
    /// </summary>
    private const int PointerOffset = 16;

    /// <summary>
    /// Minimum distance kept between the toolbar and the edge of the working area, in physical pixels.
    /// </summary>
    private const int EdgeMargin = 6;

    /// <summary>
    /// Where the window is parked for its first measurement pass, far outside any real display so the
    /// unpositioned frame is never visible.
    /// </summary>
    private static readonly PixelPoint OffScreenPosition = new(-32000, -32000);

    /// <summary>Gets the shared presentation control.</summary>
    public TextSelectionToolbar Toolbar => ToolbarRoot;

    /// <summary>Raised as toolbar content or monitor scaling changes the native hit-test region.</summary>
    public event Action<PixelRect>? BoundsChanged;

    /// <summary>
    /// Raised when the user picks an action. The toolbar does not act on the selection itself.
    /// </summary>
    public event Action<Strategy>? ActionInvoked;

    private readonly IWindowHelper _windowHelper;

    private PixelPoint _anchor;
    private IReadOnlyList<TextSelectionToolbarItem> _items = [];

    public TextSelectionToolbarWindow(IWindowHelper windowHelper)
    {
        _windowHelper = windowHelper;
        InitializeComponent();

        windowHelper.SetWindowProperties(this, focusable: false, layer: WindowLayer.Overlay);
        ToolbarRoot.ActionInvoked += HandleActionInvoked;
        SizeChanged += (_, _) => UpdateBounds();
        ScalingChanged += (_, _) => UpdateBounds();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The window lives for the lifetime of the application; only a real shutdown may close it.
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Shows the toolbar with <paramref name="items"/>, positioned near <paramref name="anchor"/>.
    /// </summary>
    /// <param name="items">Tools to offer. Must not be empty.</param>
    /// <param name="anchor">Anchor point in physical screen pixels, normally the mouse pointer.</param>
    /// <returns>The bounds the toolbar occupies, in physical screen pixels.</returns>
    public PixelRect ShowFor(IReadOnlyList<TextSelectionToolbarItem> items, PixelPoint anchor)
    {
        _anchor = anchor;
        _items = items;
        ToolbarRoot.Actions = items.AsValueEnumerable().Select(item => item.Action).ToArray();
        ToolbarRoot.CloseMenu();

        if (!IsVisible)
        {
            // The window has no platform handle until it is shown, and styles (including the
            // DynamicResource-driven padding and corner radius) are not applied before then, so its
            // content cannot be measured reliably yet. Show it off-screen first, where the
            // pre-measurement position is invisible, then measure and move it into place.
            Position = OffScreenPosition;
            Show();
        }

        Reposition(anchor, MeasuredPixelSize());

        // Re-read the size after positioning. SizeToContent negotiates the size with the platform
        // window, which is not guaranteed to have completed during the first layout pass after Show,
        // and DesktopScaling only reflects the target monitor once the window sits on it. Both make the
        // pre-move measurement unreliable, and the result is used as a hit-test rectangle by
        // IOverlayInputWatcher: too small a rectangle would classify a click on our own button as
        // "outside" and dismiss the toolbar before the button ever sees it.
        var bounds = Reposition(anchor, MeasuredPixelSize());
        _windowHelper.RaiseWindow(this);

        return bounds;
    }

    private void HandleActionInvoked(TextSelectionToolbarAction action)
    {
        // Execute the snapshot matched for this selection, not a strategy reconstructed from preview metadata.
        var item = _items.FirstOrDefault(item => ReferenceEquals(item.Action, action));
        if (item is not null)
        {
            ActionInvoked?.Invoke(item.Strategy);
        }
    }

    private void UpdateBounds()
    {
        if (IsVisible) BoundsChanged?.Invoke(Reposition(_anchor, PixelSize.FromSize(Bounds.Size, DesktopScaling)));
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _items = [];
        ToolbarRoot.Actions = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// The current content size in physical pixels, after forcing layout to settle.
    /// </summary>
    private PixelSize MeasuredPixelSize()
    {
        UpdateLayout();

        var size = PixelSize.FromSize(Bounds.Size, DesktopScaling);

        // Guard against a degenerate measurement: better to fall back to the natural size of the
        // content than to hand out an empty rectangle.
        if (size is { Width: > 0, Height: > 0 }) return size;

        ToolbarRoot.Measure(Size.Infinity);
        var desired = ToolbarRoot.DesiredSize;
        return new PixelSize(
            Math.Max(1, (int)Math.Ceiling(desired.Width * DesktopScaling)),
            Math.Max(1, (int)Math.Ceiling(desired.Height * DesktopScaling)));
    }

    /// <summary>
    /// Places the toolbar below the anchor, flipping above it when there is no room, and clamps the
    /// result into the working area of the screen containing the anchor.
    /// </summary>
    private PixelRect Reposition(PixelPoint anchor, PixelSize size)
    {
        // Pick the screen from the anchor, not the primary screen: with multiple monitors the
        // selection may be anywhere, and each monitor may have its own scaling and working area.
        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var workingArea = screen?.WorkingArea ?? new PixelRect(anchor, size);

        var x = anchor.X - size.Width / 2;
        var y = anchor.Y + PointerOffset;

        // Flip above the anchor when the preferred placement would run off the bottom edge.
        if (y + size.Height + EdgeMargin > workingArea.Bottom)
        {
            y = anchor.Y - PointerOffset - size.Height;
        }

        x = Math.Clamp(x, workingArea.X + EdgeMargin, Math.Max(workingArea.X + EdgeMargin, workingArea.Right - size.Width - EdgeMargin));
        y = Math.Clamp(y, workingArea.Y + EdgeMargin, Math.Max(workingArea.Y + EdgeMargin, workingArea.Bottom - size.Height - EdgeMargin));

        Position = new PixelPoint(x, y);
        return new PixelRect(Position, size);
    }
}