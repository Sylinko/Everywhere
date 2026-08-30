using System.Threading.Channels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Common;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// A high-performance, cross-monitor visual effect manager that orchestrates flying particle animations
/// from original screen positions into target UI attachments or regions within the ChatWindow. 
/// Designed as a singleton service.
/// </summary>
/// <remarks>
/// This system supports two distinctly handled animation modes:
/// 
/// 1. Single-Element Morphing (`CreatePickEffect`):
///    Triggered when a user selects a specific visual element on screen. A snapshot is captured, 
///    and a UI particle dynamically morphs (fades and scales) from the raw image bounds into its 
///    final DataContext-bound destination (e.g., a `ChatAttachment` chip) while tracking the window.
///    
/// 2. Multi-Element Swarm (`ScanEffectScope` / `VisualContextBuilder`):
///    Used during automated visual tree building. Employs a DPI-aware, batched TopLevel screenshot strategy
///    where hundreds of `IImage` sub-crops are fired sequentially based on a heuristic queue. 
///    The physics engine applies lateral scattering ("flocking") and Hooke's Law spring dynamics to 
///    absorb particles seamlessly behind the chatbot mascot (Eva). Masking is handled via a transparent Overlay window.
/// </remarks>
public sealed class VisualElementEffect(
    IVisualElementAnimationTarget animationTarget,
    ILogger<VisualElementEffect> logger
)
{
    private readonly IVisualElementAnimationTarget _animationTarget = animationTarget;
    private readonly List<VisualElementEffectWindow> _effectWindows = [];

    public async Task CreatePickEffect(VisualElement visualElement, ChatAttachment chatAttachment)
    {
        try
        {
            if (_effectWindows.Count == 0)
            {
                chatAttachment.Opacity = 1d;
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (!_animationTarget.IsKeyboardFocusWithin)
            {
                chatAttachment.Opacity = 1d;
                return;
            }

            var (sourceBounds, startBitmap) = await Task.Run(async () =>
            {
                var bounds = visualElement.Query(new VisualElementQueryRequest(VisualElementFields.Bounds, 0)).Snapshot.Bounds.GetValueOrDefault();
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return (bounds, null);
                }

                return (bounds, await CreateStartBitmapAsync(visualElement));
            }).WaitAsync(TimeSpan.FromSeconds(3));
            if (startBitmap is null)
            {
                chatAttachment.Opacity = 1d;
                return;
            }

            foreach (var effectWindow in _effectWindows)
            {
#if !IsMacOS
                effectWindow.Topmost = false;
                effectWindow.Topmost = true; // Ensure the effect window is above all others to properly display the animation 
#endif

                var sourceCenter = new PixelPoint(sourceBounds.Center.X, sourceBounds.Center.Y);
                var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
                var startSize = new Size(
                    Math.Max(16, sourceBounds.Width / effectWindow.Scale),
                    Math.Max(16, sourceBounds.Height / effectWindow.Scale));

                var tracker = new RunOnceTracker(this, chatAttachment, _animationTarget);
                effectWindow.AddParticle<PickVisualElementParticle>(
                    startPoint,
                    tracker,
                    startBitmap,
                    chatAttachment,
                    startSize);
            }
        }
        catch
        {
            chatAttachment.Opacity = 1d;
        }
    }

    private static async Task<Bitmap?> CreateStartBitmapAsync(VisualElement visualElement)
    {
        try
        {
            using var pointer = await visualElement.CaptureAsync(CancellationToken.None);
            return pointer.ToAvaloniaBitmap();
        }
        catch
        {
            return null;
        }
    }

    public ScanEffectScope CreateScanEffect(VisualContext context, CancellationToken cancellationToken) => new(this, logger, context.CreateRetention(), cancellationToken);

    public void ArrangeEffectWindows()
    {
        var screens = App.Screens.All;
        if (screens is not { Count: > 0 })
        {
            foreach (var effectWindow in _effectWindows) effectWindow.Close();
            _effectWindows.Clear();
            return;
        }

        var i = 0;
        for (; i < screens.Count; i++)
        {
            VisualElementEffectWindow effectWindow;
            if (_effectWindows.Count > i)
            {
                effectWindow = _effectWindows[i];
            }
            else
            {
                effectWindow = new VisualElementEffectWindow();
                _effectWindows.Add(effectWindow);
            }

            effectWindow.SetPlacement(screens[i]);
            effectWindow.Show();
        }

        // Remove unnecessary VisualElementEffectWindow
        for (var j = _effectWindows.Count - 1; j >= i; j--)
        {
            _effectWindows[j].Close();
            _effectWindows.RemoveAt(j);
        }
    }

    /// <summary>
    /// Owns the asynchronous visual-effect queue and every element retained while that queue is active.
    /// </summary>
    public sealed class ScanEffectScope(
        VisualElementEffect owner,
        ILogger logger,
        VisualElementRetention retention,
        CancellationToken cancellationToken
    ) : IDisposable
    {

        private readonly HashSet<nint> _emittedWindowHandles = [];
        private readonly Channel<VisualElementQueryResult> _emissionQueue = Channel.CreateBounded<VisualElementQueryResult>(
            new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                SingleWriter = false
            });

        private readonly CancellationTokenSource _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private int _completionState;

        public void Add(VisualElementQueryResult queryResult)
        {
            if (Volatile.Read(ref _completionState) != 0) return;
            if (!_emissionQueue.Writer.TryWrite(queryResult)) return;
            retention.Retain(queryResult.Element);
        }

        /// <summary>
        /// Completes successful production and starts draining the queued visual effects.
        /// </summary>
        /// <remarks>
        /// Consumption starts only after production so the effect does not mutate the same VisualContext concurrently with Snapshot traversal.
        /// </remarks>
        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0) return;
            _emissionQueue.Writer.TryComplete();
            Task.Run(() => EmissionLoopAsync(_cancellationTokenSource.Token), CancellationToken.None).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        /// <summary>
        /// Cancels an incomplete effect scope so its operation-level element retention can be released promptly.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _completionState, 2, 0) != 0) return;
            _emissionQueue.Writer.TryComplete();
            retention.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private async Task EmissionLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(owner.ArrangeEffectWindows, DispatcherPriority.Render, cancellationToken);

                while (await _emissionQueue.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (_emissionQueue.Reader.TryRead(out var queryResult))
                    {
                        if (owner._effectWindows.Count == 0) return;

                        try
                        {
                            if (GetTopLevel(queryResult) is not { } topLevelResult) continue;
                            var topLevel = topLevelResult.Element;
                            var topLevelSnapshot = topLevelResult.Snapshot;
                            if (topLevelSnapshot.States.GetValueOrDefault().HasFlag(VisualElementStates.Offscreen)) continue;

                            var windowHandle = topLevelSnapshot.NativeWindowHandle.GetValueOrDefault();
                            if (windowHandle == 0) continue; // Allow screen (-1)

                            if (!_emittedWindowHandles.Add(windowHandle)) continue; // Already emitted

                            var boundingRectangle = topLevelSnapshot.Bounds.GetValueOrDefault();
                            if (boundingRectangle.Width <= 16 || boundingRectangle.Height <= 16) continue;

                            SKImage? topLevelImage;
                            try
                            {
                                using var pointer = await topLevel.CaptureAsync(cancellationToken);
                                topLevelImage = pointer.ToSKImage();
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to capture TopLevel for visual effect. {NativeWindowHandle}", windowHandle);
                                continue;
                            }

                            if (topLevelImage is null) continue;

                            await Dispatcher.UIThread.InvokeAsync(
                                () => EmitParticle(boundingRectangle, topLevelImage),
                                DispatcherPriority.Render,
                                cancellationToken);
                        }
                        catch (Exception)
                        {
                            logger.LogWarning("Failed to emit visual element particle for element {ElementId}", queryResult.Element.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in visual element effect emission loop");
            }
            finally
            {
                Interlocked.Exchange(ref _completionState, 2);
                _emissionQueue.Writer.TryComplete();
                retention.Dispose();
                _cancellationTokenSource.Dispose();
            }
        }

        private VisualElementQueryResult? GetTopLevel(VisualElementQueryResult current)
        {
            var node = current;
            while (true)
            {
                if (node.Snapshot.Type == VisualElementType.TopLevel) return node;
                using var parentEnumerator = node.Element.CreateEnumerator(VisualElementRelation.Parent, new VisualElementEnumerationOptions(VisualElementQueryRequest.Default));
                if (!parentEnumerator.MoveNext()) return null;
                node = parentEnumerator.Current;
                retention.Retain(node.Element);
            }
        }

        private void EmitParticle(PixelRect bounds, SKImage image)
        {
            foreach (var effectWindow in owner._effectWindows)
            {
                effectWindow.Topmost = false;
                effectWindow.Topmost = true; // Ensure the effect window is above all others to properly display the animation

                var sourceCenter = new PixelPoint(bounds.Center.X, bounds.Center.Y);
                var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
                var startSize = new Size(
                    Math.Max(16, bounds.Width / effectWindow.Scale),
                    Math.Max(16, bounds.Height / effectWindow.Scale));

                effectWindow.AddParticle<ScanVisualElementParticle>(
                    startPoint,
                    null,
                    image,
                    null,
                    startSize);
            }
        }
    }

    private sealed class RunOnceTracker(
        VisualElementEffect owner,
        ChatAttachment chatAttachment,
        IVisualElementAnimationTarget target
    ) : IParticleTargetTracker
    {
        public bool IsCancelled => !target.IsVisible;

        public bool TryGetTargetCenterOnScreen(out PixelPoint point) =>
            owner._animationTarget.TryGetAttachmentCenterOnScreen(chatAttachment, out point);

        public void OnParticleCompleted()
        {
            chatAttachment.Opacity = 1d;
        }
    }
}
