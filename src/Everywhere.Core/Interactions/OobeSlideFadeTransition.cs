using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Everywhere.Interactions;

/// <summary>
/// Transitions between onboarding pages by sliding both outgoing and incoming views horizontally
/// over a bounded distance while cross-fading opacity so motion stays unclipped inside host margins.
/// </summary>
public sealed class OobeSlideFadeTransition : IPageTransition
{
    /// <summary>
    /// Gets or sets the duration of the slide and fade transition.
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(240);

    /// <summary>
    /// Gets or sets the maximum horizontal travel distance in device-independent pixels.
    /// </summary>
    public double MaxSlideDistance { get; set; } = 96d;

    /// <summary>
    /// Gets or sets the fraction of host width used to compute horizontal travel distance.
    /// </summary>
    public double SlideWidthRatio { get; set; } = 0.14d;

    /// <summary>
    /// Gets or sets whether outgoing and incoming views translate horizontally during transition.
    /// </summary>
    public bool IsSlideEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the easing curve applied to the horizontal translation.
    /// </summary>
    public Easing SlideEasing { get; set; } = new CubicEaseOut();

    /// <summary>
    /// Gets or sets the easing curve applied to opacity transitions.
    /// </summary>
    public Easing FadeEasing { get; set; } = new CubicEaseInOut();

    /// <inheritdoc />
    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        if (!IsSlideEnabled)
        {
            if (to is not null)
            {
                to.IsVisible = true;
            }

            if (from is not null)
            {
                from.IsVisible = false;
            }

            return;
        }

        var host = (from ?? to)?.GetVisualParent();
        var hostWidth = host?.Bounds.Width ?? 0d;
        var distance = hostWidth > 0d
            ? Math.Clamp(hostWidth * SlideWidthRatio, 48d, MaxSlideDistance)
            : MaxSlideDistance;
        var offset = forward ? distance : -distance;

        var animations = new List<Task>(4);

        if (from is not null)
        {
            animations.Add(CreateTranslateAnimation(0d, -offset).RunAsync(from, cancellationToken));
            animations.Add(CreateFadeAnimation(1d, 0d, 0.8d, 0d).RunAsync(from, cancellationToken));
        }

        if (to is not null)
        {
            to.IsVisible = true;
            animations.Add(CreateTranslateAnimation(offset, 0d).RunAsync(to, cancellationToken));
            animations.Add(CreateFadeAnimation(0d, 0.15d, 1d, 1d).RunAsync(to, cancellationToken));
        }

        await Task.WhenAll(animations);

        if (from is not null && !cancellationToken.IsCancellationRequested)
        {
            from.IsVisible = false;
        }
    }

    private Animation CreateTranslateAnimation(double fromX, double toX) => new()
    {
        Duration = Duration,
        Easing = SlideEasing,
        FillMode = FillMode.None,
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters = { new Setter(TranslateTransform.XProperty, fromX) }
            },
            new KeyFrame
            {
                Cue = new Cue(1d),
                Setters = { new Setter(TranslateTransform.XProperty, toX) }
            }
        }
    };

    private Animation CreateFadeAnimation(double startOpacity, double holdCue, double fadeCue, double endOpacity)
    {
        var animation = new Animation
        {
            Duration = Duration,
            Easing = FadeEasing,
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(Visual.OpacityProperty, startOpacity) }
                }
            }
        };

        if (holdCue > 0d)
        {
            animation.Children.Add(
                new KeyFrame
                {
                    Cue = new Cue(holdCue),
                    Setters = { new Setter(Visual.OpacityProperty, startOpacity) }
                });
        }

        animation.Children.Add(
            new KeyFrame
            {
                Cue = new Cue(fadeCue),
                Setters = { new Setter(Visual.OpacityProperty, endOpacity) }
            });

        if (fadeCue < 1d)
        {
            animation.Children.Add(
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(Visual.OpacityProperty, endOpacity) }
                });
        }

        return animation;
    }
}
