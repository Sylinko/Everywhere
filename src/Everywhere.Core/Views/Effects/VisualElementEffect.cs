using System.Threading.Channels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Interop;
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

    public async Task CreatePickEffect(IVisualElement visualElement, ChatAttachment chatAttachment)
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
                var bounds = visualElement.BoundingRectangle;
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

    private static async Task<Bitmap?> CreateStartBitmapAsync(IVisualElement visualElement)
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

    public sealed class ScanEffectScope
    {
        private readonly VisualElementEffect _owner;
        private readonly ILogger _logger;

        private readonly HashSet<nint> _emittedWindowHandles = [];
        private readonly Channel<IVisualElement> _emissionQueue = Channel.CreateBounded<IVisualElement>(
            new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                SingleWriter = false
            });

        public ScanEffectScope(VisualElementEffect owner, ILogger logger, CancellationToken cancellationToken)
        {
            _owner = owner;
            _logger = logger;

            Task.Run(() => EmissionLoopAsync(cancellationToken), cancellationToken).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        public void Add(IVisualElement element)
        {
            _emissionQueue.Writer.TryWrite(element);
        }

        private async Task EmissionLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(_owner.ArrangeEffectWindows, DispatcherPriority.Render, cancellationToken);

                while (await _emissionQueue.Reader.WaitToReadAsync(cancellationToken))
                {
                    while (_emissionQueue.Reader.TryRead(out var element))
                    {
                        if (_owner._effectWindows.Count == 0) return;

                        try
                        {
                            if (GetTopLevel(element) is not { } topLevel) continue;
                            if (topLevel.States.HasFlag(VisualElementStates.Offscreen)) continue;

                            var windowHandle = topLevel.NativeWindowHandle;
                            if (windowHandle == 0) continue; // Allow screen (-1)

                            if (!_emittedWindowHandles.Add(windowHandle)) continue; // Already emitted

                            var boundingRectangle = topLevel.BoundingRectangle;
                            if (boundingRectangle.Width <= 16 || boundingRectangle.Height <= 16) continue;

                            SKImage? topLevelImage;
                            try
                            {
                                using var pointer = await topLevel.CaptureAsync(cancellationToken);
                                topLevelImage = pointer.ToSKImage();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to capture TopLevel for visual effect. {NativeWindowHandle}", windowHandle);
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
                            _logger.LogWarning("Failed to emit visual element particle for element {ElementId}", element.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in visual element effect emission loop");
            }
        }

        private static IVisualElement? GetTopLevel(IVisualElement current)
        {
            var node = current;
            while (node != null)
            {
                if (node.Type == VisualElementType.TopLevel) return node;
                node = node.Parent;
            }

            return null;
        }

        private void EmitParticle(PixelRect bounds, SKImage image)
        {
            foreach (var effectWindow in _owner._effectWindows)
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