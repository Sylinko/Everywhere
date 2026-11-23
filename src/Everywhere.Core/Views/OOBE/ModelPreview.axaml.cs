using System.Numerics;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Everywhere.Views;

/// <summary>
/// Animated preview illustrating model switching inside a picker where the purple highlight slides
/// smoothly onto the target option and its checkmark resolves while brand logos remain stationary (~0.9s).
/// </summary>
public sealed partial class ModelPreview : UserControl, IOobePreview
{
    private const float RowStepY = 32f;
    private static readonly Vector3 CheckCenter = new(6f, 6f, 0f);
    private static readonly Vector3 ShrunkCheckScale = new(0.72f, 0.72f, 1f);

    private readonly OobePreviewPlaybackController _playback;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelPreview"/> class.
    /// </summary>
    public ModelPreview()
    {
        InitializeComponent();
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

    private void PlayCore(CancellationToken _)
    {
        if (ElementComposition.GetElementVisual(SelectionHighlight) is not { } highlightVisual ||
            ElementComposition.GetElementVisual(CheckOption1) is not { } check1Visual)
        {
            return;
        }

        var compositor = highlightVisual.Compositor;
        var easing = OobePreviewPlaybackController.CreateDecelerateEasing();

        // Highlight pill glides cleanly down from option 0 to option 1 over 520ms.
        OobePreviewPlaybackController.AnimateOffset(
            highlightVisual,
            compositor,
            Vector3.Zero,
            new Vector3(0f, RowStepY, 0f),
            TimeSpan.FromMilliseconds(520),
            TimeSpan.FromMilliseconds(80),
            easing);

        // Option 1 checkmark resolves into place upon arrival (~860ms finish).
        OobePreviewPlaybackController.AnimateOpacity(
            check1Visual,
            compositor,
            0f,
            1f,
            TimeSpan.FromMilliseconds(360),
            TimeSpan.FromMilliseconds(500),
            easing);
        OobePreviewPlaybackController.AnimateScale(
            check1Visual,
            compositor,
            ShrunkCheckScale,
            Vector3.One,
            CheckCenter,
            TimeSpan.FromMilliseconds(360),
            TimeSpan.FromMilliseconds(500),
            easing);
    }

    private void StopCore()
    {
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(SelectionHighlight));
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(CheckOption1));
    }

    private void ResetCore()
    {
        if (ElementComposition.GetElementVisual(SelectionHighlight) is { } highlightVisual)
        {
            highlightVisual.Offset = new Vector3(0f, RowStepY, 0f);
        }

        if (ElementComposition.GetElementVisual(CheckOption1) is { } check1Visual)
        {
            check1Visual.Opacity = 1f;
            check1Visual.Scale = Vector3.One;
            check1Visual.CenterPoint = CheckCenter;
        }
    }
}
