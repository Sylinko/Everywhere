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
/// 2. Multi-Element Swarm (`ScanEffectScope` / visual-context Snapshot pipeline):
///    Used during automated visual-tree observation. Employs a DPI-aware, batched TopLevel screenshot strategy
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

    /// <summary>Consumes an owned capture and plays a pick animation, releasing it on every exit path.</summary>
    public async Task CreatePickEffect(IVisualElementCapture capture, ChatAttachment chatAttachment)
    {
        using var ownedCapture = capture;
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

            var sourceBounds = capture.Bounds;
            var startBitmap = capture.ToAvaloniaBitmap();
            if (startBitmap is null)
            {
                chatAttachment.Opacity = 1d;
                return;
            }

            using var image = new VisualEffectImage<Bitmap>(startBitmap);
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
                    image,
                    chatAttachment,
                    startSize);
            }
        }
        catch
        {
            chatAttachment.Opacity = 1d;
        }
    }

    /// <summary>Creates an image-consumer scope with no native-element or Context ownership.</summary>
    public ScanEffectScope CreateScanEffect(CancellationToken cancellationToken) => new(this, logger, cancellationToken);

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

    /// <summary>Owns a bounded capture queue. Complete hands draining to the consumer; disposing before Complete abandons queued images.</summary>
    public sealed class ScanEffectScope(VisualElementEffect owner, ILogger logger, CancellationToken cancellationToken) : IDisposable
    {
        // A capture can occupy 64 MiB. Do not turn the old thousand-element queue into a thousand-image queue.
        private readonly Channel<IVisualElementCapture> _emissionQueue = Channel.CreateBounded<IVisualElementCapture>(new BoundedChannelOptions(4)
        {
            SingleReader = true, SingleWriter = true
        });
        private int _completionState;

        /// <summary>Takes ownership, including when a completed, canceled, or full scope discards the capture.</summary>
        public void AddCapture(IVisualElementCapture capture)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _completionState) != 0 || !_emissionQueue.Writer.TryWrite(capture))
                capture.Dispose();
        }

        /// <summary>Completes production and drains owned images independently of the query Context.</summary>
        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0) return;
            _emissionQueue.Writer.TryComplete();
            Task.Run(EmissionLoopAsync, CancellationToken.None).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        /// <summary>Discards incomplete production; after Complete the consumer owns cleanup.</summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _completionState, 2, 0) != 0) return;
            _emissionQueue.Writer.TryComplete();
            Drain();
        }

        private void Drain()
        {
            while (_emissionQueue.Reader.TryRead(out var capture)) capture.Dispose();
        }

        private async Task EmissionLoopAsync()
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(owner.ArrangeEffectWindows, DispatcherPriority.Render, cancellationToken);
                while (_emissionQueue.Reader.TryRead(out var capture))
                {
                    using (capture)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (capture.Bounds.Width <= 16 || capture.Bounds.Height <= 16) continue;

                        var image = capture.ToSKImage();
                        if (image is null) continue;

                        using var sharedImage = new VisualEffectImage<SKImage>(image);
                        await Dispatcher.UIThread.InvokeAsync(
                            () => EmitParticle(capture.Bounds, sharedImage),
                            DispatcherPriority.Render,
                            cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to play scan captures.");
            }
            finally
            {
                Interlocked.Exchange(ref _completionState, 2);
                Drain();
            }
        }

        private void EmitParticle(PixelRect bounds, VisualEffectImage<SKImage> image)
        {
            foreach (var effectWindow in owner._effectWindows)
            {
                effectWindow.Topmost = false;
                effectWindow.Topmost = true; // Ensure the effect window is above all others to properly display the animation

                var sourceCenter = new PixelPoint(bounds.Center.X, bounds.Center.Y);
                var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
                var startSize = new Size(Math.Max(16, bounds.Width / effectWindow.Scale), Math.Max(16, bounds.Height / effectWindow.Scale));
                effectWindow.AddParticle<ScanVisualElementParticle>(startPoint, null, image, null, startSize);
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
