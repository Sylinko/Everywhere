using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Utilities;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
///     A compact circular context-usage indicator with an optional flyout.
/// </summary>
public sealed class ContextUsageRing : ProgressRing, ICustomHitTest
{
    public static readonly StyledProperty<FlyoutBase?> FlyoutProperty =
        AvaloniaProperty.Register<ContextUsageRing, FlyoutBase?>(nameof(Flyout));

    public FlyoutBase? Flyout
    {
        get => GetValue(FlyoutProperty);
        set => SetValue(FlyoutProperty, value);
    }

    private IDisposable? _flyoutShowTimer;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);

        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
        if (Flyout is not null)
        {
            _flyoutShowTimer = DispatcherTimer.RunOnce(
                () =>
                {
                    if (Flyout is not null && IsPointerOver)
                    {
                        Flyout.ShowAt(this);
                    }
                },
                TimeSpan.FromMilliseconds(500));
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
        Flyout?.ShowAt(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
        base.OnDetachedFromVisualTree(e);
    }

    public bool HitTest(Point point) => Bounds.Contains(point);
}
