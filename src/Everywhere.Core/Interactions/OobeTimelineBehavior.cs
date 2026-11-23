using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;
using Motion.Avalonia;

namespace Everywhere.Interactions;

/// <summary>
/// Controls a <see cref="TimelineMotion"/> on an onboarding <see cref="MotionPanel"/> so playback
/// runs only while the panel and host window are visible and active. Pausing preserves timeline
/// position across hiding and minimization, while completed one-shot timelines remain finished
/// until <see cref="Replay"/> is invoked or <see cref="ReplayKey"/> changes.
/// </summary>
public sealed class OobeTimelineBehavior : Behavior<MotionPanel>
{
    /// <summary>
    /// Defines the <see cref="IsPlaying"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<OobeTimelineBehavior, bool>(nameof(IsPlaying), true);

    /// <summary>
    /// Defines the <see cref="ReplayKey"/> property identifying the active content instance.
    /// </summary>
    public static readonly StyledProperty<object?> ReplayKeyProperty =
        AvaloniaProperty.Register<OobeTimelineBehavior, object?>(nameof(ReplayKey));

    /// <summary>
    /// Gets or sets whether this animation should play, subject to window and visual visibility.
    /// </summary>
    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>
    /// Gets or sets a token whose identity change resets and replays the timeline.
    /// </summary>
    public object? ReplayKey
    {
        get => GetValue(ReplayKeyProperty);
        set => SetValue(ReplayKeyProperty, value);
    }

    private Window? _window;

    /// <summary>
    /// Restarts the timeline from frame zero without altering flow state.
    /// </summary>
    public void Replay()
    {
        if (AssociatedObject?.Motion is not TimelineMotion timeline) return;
        timeline.Time = TimeSpan.Zero;
        timeline.Player.Reset();
        UpdatePlayback();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        if (AssociatedObject is not { } panel) return;
        _window = TopLevel.GetTopLevel(panel) as Window;
        if (_window is not null) _window.PropertyChanged += HandleWindowChanged;
        panel.PropertyChanged += HandlePanelChanged;
        panel.EffectiveViewportChanged += HandleEffectiveViewportChanged;
        panel.Loaded += HandleLoaded;
        UpdatePlayback();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree()
    {
        DetachWindow();
        base.OnDetachedFromVisualTree();
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        DetachWindow();
        base.OnDetaching();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReplayKeyProperty) Replay();
        if (change.Property == IsPlayingProperty || change.Property == IsEnabledProperty) UpdatePlayback();
    }

    private void HandleWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Window.WindowStateProperty || args.Property == Visual.IsVisibleProperty)
        {
            UpdatePlayback();
        }
    }

    private void HandlePanelChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty)
        {
            UpdatePlayback();
        }
    }

    private void HandleEffectiveViewportChanged(object? sender, EventArgs args) => UpdatePlayback();

    private void HandleLoaded(object? sender, RoutedEventArgs args)
    {
        // Transitioning templates can attach before becoming effectively visible; Loaded
        // synchronizes playback state once mounted into the active visual tree.
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        if (AssociatedObject?.Motion is not TimelineMotion timeline) return;
        if (IsEnabled &&
            IsPlaying &&
            AssociatedObject.IsEffectivelyVisible &&
            _window is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            if (timeline.Loop || timeline.Time < timeline.Duration)
            {
                timeline.Player.Play();
            }
        }
        else
        {
            timeline.Player.Pause();
        }
    }

    private void DetachWindow()
    {
        if (_window is not null) _window.PropertyChanged -= HandleWindowChanged;
        _window = null;
        if (AssociatedObject is not { } panel) return;
        panel.PropertyChanged -= HandlePanelChanged;
        panel.EffectiveViewportChanged -= HandleEffectiveViewportChanged;
        panel.Loaded -= HandleLoaded;
        if (panel.Motion is TimelineMotion timeline) timeline.Player.Pause();
    }
}
