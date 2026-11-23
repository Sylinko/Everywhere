using System.Numerics;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Everywhere.Views;

/// <summary>
/// Coordinates visual-tree attachment playback, cancellation, and shared Avalonia Composition
/// animation helpers for onboarding preview controls.
/// </summary>
internal sealed class OobePreviewPlaybackController(
    Action<CancellationToken> playCore,
    Action stopCore,
    Action resetCore
)
{
    private CancellationTokenSource? _playbackCts;
    private bool _isAttached;
    private bool _isPlayPending;

    public void Attach()
    {
        if (_isAttached) return;
        _isAttached = true;
        resetCore();

        _isPlayPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_isAttached || !_isPlayPending) return;
                _isPlayPending = false;
                Play();
            },
            DispatcherPriority.Loaded);
    }

    public void Detach()
    {
        _isAttached = false;
        _isPlayPending = false;
        Stop();
    }

    public void Play()
    {
        if (!_isAttached) return;

        _isPlayPending = false;

        CancelPlayback();
        stopCore();

        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        playCore(cts.Token);
    }

    public void Stop()
    {
        _isPlayPending = false;
        CancelPlayback();
        stopCore();
    }

    public void Reset()
    {
        _isPlayPending = false;
        CancelPlayback();
        stopCore();
        resetCore();
    }

    private void CancelPlayback()
    {
        if (_playbackCts is null) return;
        _playbackCts.Cancel();
        _playbackCts.Dispose();
        _playbackCts = null;
    }

    public static IEasing CreateDecelerateEasing() => new CubicEaseOut();

    public static void StopScalarAndVectorAnimations(CompositionVisual? visual)
    {
        if (visual is null) return;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        visual.StopAnimation("Scale");
    }

    public static void AnimateOpacity(
        CompositionVisual visual,
        Compositor compositor,
        float from,
        float to,
        TimeSpan duration,
        TimeSpan delay,
        IEasing? easing = null
    )
    {
        visual.Opacity = from;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Opacity";
        animation.DelayTime = delay;
        animation.Duration = duration;
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, easing ?? CreateDecelerateEasing());
        visual.StartAnimation("Opacity", animation);
    }

    public static void AnimateOffset(
        CompositionVisual visual,
        Compositor compositor,
        Vector3 from,
        Vector3 to,
        TimeSpan duration,
        TimeSpan delay,
        IEasing? easing = null
    )
    {
        visual.Offset = from;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Offset";
        animation.DelayTime = delay;
        animation.Duration = duration;
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, easing ?? CreateDecelerateEasing());
        visual.StartAnimation("Offset", animation);
    }

    public static void AnimateScale(
        CompositionVisual visual,
        Compositor compositor,
        Vector3 from,
        Vector3 to,
        Vector3 centerPoint,
        TimeSpan duration,
        TimeSpan delay,
        IEasing? easing = null
    )
    {
        visual.CenterPoint = centerPoint;
        visual.Scale = from;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.DelayTime = delay;
        animation.Duration = duration;
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, easing ?? CreateDecelerateEasing());
        visual.StartAnimation("Scale", animation);
    }
}
