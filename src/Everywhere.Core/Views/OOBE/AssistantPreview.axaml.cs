using System.Numerics;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Everywhere.Views;

/// <summary>
/// Animated preview illustrating a calm floating assistant window opening over a static app,
/// followed by a user question and softly streaming reply lines (~1.5s via Composition API).
/// </summary>
public sealed partial class AssistantPreview : UserControl, IOobePreview
{
    private readonly OobePreviewPlaybackController _playback;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssistantPreview"/> class.
    /// </summary>
    public AssistantPreview()
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
        if (ElementComposition.GetElementVisual(AssistantOverlay) is not { } overlayVisual ||
            ElementComposition.GetElementVisual(QuestionBubble) is not { } questionVisual ||
            ElementComposition.GetElementVisual(ReplyLine1) is not { } reply1Visual ||
            ElementComposition.GetElementVisual(ReplyLine2) is not { } reply2Visual ||
            ElementComposition.GetElementVisual(ReplyLine3) is not { } reply3Visual)
        {
            return;
        }

        var compositor = overlayVisual.Compositor;
        var easing = OobePreviewPlaybackController.CreateDecelerateEasing();
        var bounds = AssistantOverlay.Bounds;
        var center = new Vector3((float)bounds.Width * 0.5f, (float)bounds.Height * 0.5f, 0f);

        // Window entrance: steady 0.96 -> 1.0 scale + 8px vertical lift over 300ms (no spring bounce).
        OobePreviewPlaybackController.AnimateOpacity(
            overlayVisual,
            compositor,
            0f,
            1f,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.Zero,
            easing);
        OobePreviewPlaybackController.AnimateScale(
            overlayVisual,
            compositor,
            new Vector3(0.96f, 0.96f, 1f),
            Vector3.One,
            center,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.Zero,
            easing);
        OobePreviewPlaybackController.AnimateOffset(
            overlayVisual,
            compositor,
            new Vector3(0f, 8f, 0f),
            Vector3.Zero,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.Zero,
            easing);

        // User prompt bubble fades in cleanly after the overlay settles.
        OobePreviewPlaybackController.AnimateOpacity(
            questionVisual,
            compositor,
            0f,
            1f,
            TimeSpan.FromMilliseconds(260),
            TimeSpan.FromMilliseconds(340),
            easing);
        OobePreviewPlaybackController.AnimateOffset(
            questionVisual,
            compositor,
            new Vector3(0f, 4f, 0f),
            Vector3.Zero,
            TimeSpan.FromMilliseconds(260),
            TimeSpan.FromMilliseconds(340),
            easing);

        // Staggered answer lines fade in sequentially through ~1.5s.
        OobePreviewPlaybackController.AnimateOpacity(
            reply1Visual,
            compositor,
            0f,
            1f,
            TimeSpan.FromMilliseconds(340),
            TimeSpan.FromMilliseconds(640),
            easing);
        OobePreviewPlaybackController.AnimateOpacity(
            reply2Visual,
            compositor,
            0f,
            1f,
            TimeSpan.FromMilliseconds(340),
            TimeSpan.FromMilliseconds(880),
            easing);
        OobePreviewPlaybackController.AnimateOpacity(
            reply3Visual,
            compositor,
            0f,
            1f,
            TimeSpan.FromMilliseconds(380),
            TimeSpan.FromMilliseconds(1120),
            easing);
    }

    private void StopCore()
    {
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(AssistantOverlay));
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(QuestionBubble));
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(ReplyLine1));
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(ReplyLine2));
        OobePreviewPlaybackController.StopScalarAndVectorAnimations(ElementComposition.GetElementVisual(ReplyLine3));
    }

    private void ResetCore()
    {
        RestoreAnimatedContainerVisual(ElementComposition.GetElementVisual(AssistantOverlay));
        RestoreAnimatedContainerVisual(ElementComposition.GetElementVisual(QuestionBubble));
        RestoreFadeVisual(ElementComposition.GetElementVisual(ReplyLine1));
        RestoreFadeVisual(ElementComposition.GetElementVisual(ReplyLine2));
        RestoreFadeVisual(ElementComposition.GetElementVisual(ReplyLine3));
    }

    private static void RestoreAnimatedContainerVisual(CompositionVisual? visual)
    {
        if (visual is null) return;
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;
        visual.Offset = Vector3.Zero;
    }

    private static void RestoreFadeVisual(CompositionVisual? visual)
    {
        visual?.Opacity = 1f;
    }
}
