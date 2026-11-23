using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Everywhere.Views;

/// <summary>
/// Animated preview illustrating progressive text selection followed by a floating context toolbar.
/// Uses native Avalonia KeyFrame animations over ~1.2 seconds.
/// </summary>
public sealed partial class ContextPreview : UserControl, IOobePreview
{
    private const double SelectionTargetWidth = 108d;
    private static readonly TimeSpan TotalDuration = TimeSpan.FromMilliseconds(1200);

    private readonly OobePreviewPlaybackController _playback;
    private readonly Animation _selectionAnimation;
    private readonly Animation _toolbarOffsetAnimation;
    private readonly Animation _toolbarRevealAnimation;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextPreview"/> class.
    /// </summary>
    public ContextPreview()
    {
        InitializeComponent();

        var easeOut = new CubicEaseOut();
        var easeInOut = new CubicEaseInOut();

        _selectionAnimation = new Animation
        {
            Duration = TotalDuration,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(WidthProperty, 0d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.38d),
                    Setters = { new Setter(WidthProperty, SelectionTargetWidth) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(WidthProperty, SelectionTargetWidth) }
                }
            },
            Easing = easeInOut
        };

        _toolbarOffsetAnimation = new Animation
        {
            Duration = TotalDuration,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(TranslateTransform.YProperty, 5d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.30d),
                    Setters = { new Setter(TranslateTransform.YProperty, 5d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.64d),
                    Setters = { new Setter(TranslateTransform.YProperty, -1d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.76d),
                    Setters = { new Setter(TranslateTransform.YProperty, 0d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(TranslateTransform.YProperty, 0d) }
                }
            },
            Easing = easeOut
        };

        _toolbarRevealAnimation = new Animation
        {
            Duration = TotalDuration,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d),
                        new Setter(ScaleTransform.ScaleXProperty, 0.88d),
                        new Setter(ScaleTransform.ScaleYProperty, 0.88d)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.30d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d),
                        new Setter(ScaleTransform.ScaleXProperty, 0.88d),
                        new Setter(ScaleTransform.ScaleYProperty, 0.88d)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.64d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(ScaleTransform.ScaleXProperty, 1.03d),
                        new Setter(ScaleTransform.ScaleYProperty, 1.03d)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.76d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(ScaleTransform.ScaleXProperty, 1d),
                        new Setter(ScaleTransform.ScaleYProperty, 1d)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(ScaleTransform.ScaleXProperty, 1d),
                        new Setter(ScaleTransform.ScaleYProperty, 1d)
                    }
                }
            },
            Easing = easeOut
        };

        _playback = new OobePreviewPlaybackController(PlayCore, StopCore, ResetCore);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _playback.Attach();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _playback.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    public void Play() => _playback.Play();

    /// <inheritdoc />
    public void Stop() => _playback.Stop();

    /// <inheritdoc />
    public void Reset() => _playback.Reset();

    private void PlayCore(CancellationToken cancellationToken)
    {
        SelectionHighlight.Width = 0d;
        FloatingToolbar.Opacity = 0d;
        if (FloatingToolbar.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 0.90d;
            scale.ScaleY = 0.90d;
        }
        if (FloatingToolbarHost.RenderTransform is TranslateTransform translate)
        {
            translate.Y = 5d;
        }

        RunAnimationSequenceAsync(cancellationToken).Detach();
    }

    private async Task RunAnimationSequenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(
                _selectionAnimation.RunAsync(SelectionHighlight, cancellationToken),
                _toolbarOffsetAnimation.RunAsync(FloatingToolbarHost, cancellationToken),
                _toolbarRevealAnimation.RunAsync(FloatingToolbar, cancellationToken));

            if (!cancellationToken.IsCancellationRequested)
            {
                ResetCore();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly by Stop/Reset/Replay.
        }
    }

    private static void StopCore()
    {
    }

    private void ResetCore()
    {
        SelectionHighlight.Width = SelectionTargetWidth;
        FloatingToolbar.Opacity = 1d;
        if (FloatingToolbar.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 1d;
            scale.ScaleY = 1d;
        }
        if (FloatingToolbarHost.RenderTransform is TranslateTransform translate)
        {
            translate.Y = 0d;
        }
    }
}
