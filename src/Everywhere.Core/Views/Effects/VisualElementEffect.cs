using System.Threading.Channels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>Owns the shared cross-monitor overlay windows and pooled particles used by visual-element effects.</summary>
public sealed class VisualElementEffect(IWindowHelper windowHelper, ILogger<VisualElementEffect> logger) : IVisualContextScanEffect
{
    private readonly List<VisualElementEffectWindow> _effectWindows = [];

    /// <summary>Starts a prepared capture-to-attachment animation on every effect window.</summary>
    /// <returns><see langword="true" /> when at least one particle was started.</returns>
    public bool PlayPickEffect(
        PixelRect sourceBounds,
        Bitmap startContent,
        object endContent,
        IParticleTargetTracker targetTracker)
    {
        if (_effectWindows.Count == 0) return false;

        foreach (var effectWindow in _effectWindows)
        {
            var sourceCenter = new PixelPoint(sourceBounds.Center.X, sourceBounds.Center.Y);
            var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
            var startSize = new Size(
                Math.Max(1d, sourceBounds.Width / effectWindow.Scale),
                Math.Max(1d, sourceBounds.Height / effectWindow.Scale));
            effectWindow.AddParticle<PickVisualElementParticle>(
                startPoint,
                targetTracker,
                startContent,
                endContent,
                startSize);
            ShowAndRaise(effectWindow);
        }

        return true;
    }

    /// <inheritdoc />
    public IVisualContextScanScope Begin(CancellationToken cancellationToken) =>
        new ScanEffectScope(this, logger, cancellationToken);

    /// <summary>Creates, removes, and positions passive effect windows for the current displays.</summary>
    public void ArrangeEffectWindows()
    {
        var screens = App.Screens.All;
        if (screens is not { Count: > 0 })
        {
            foreach (var effectWindow in _effectWindows) effectWindow.Close();
            _effectWindows.Clear();
            return;
        }

        var index = 0;
        for (; index < screens.Count; index++)
        {
            VisualElementEffectWindow effectWindow;
            if (_effectWindows.Count > index)
            {
                effectWindow = _effectWindows[index];
            }
            else
            {
                effectWindow = new VisualElementEffectWindow();
                windowHelper.InitializeWindow(effectWindow);
                windowHelper.SetWindowProperties(effectWindow, focusable: false, hitTestVisible: false, WindowLayer.Overlay);
                _effectWindows.Add(effectWindow);
            }

            effectWindow.SetPlacement(screens[index]);
        }

        for (var extraIndex = _effectWindows.Count - 1; extraIndex >= index; extraIndex--)
        {
            _effectWindows[extraIndex].Close();
            _effectWindows.RemoveAt(extraIndex);
        }
    }

    private void ShowAndRaise(VisualElementEffectWindow effectWindow)
    {
        if (!effectWindow.IsVisible) effectWindow.Show();
        windowHelper.RaiseWindow(effectWindow);
    }

    private sealed class ScanEffectScope : IVisualContextScanScope
    {
        private readonly VisualElementEffect _owner;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetimeCancellation;
        private readonly Channel<IVisualElementCapture> _captures = Channel.CreateBounded<IVisualElementCapture>(
            new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

        private int _completionState;

        public ScanEffectScope(VisualElementEffect owner, ILogger logger, CancellationToken cancellationToken)
        {
            _owner = owner;
            _logger = logger;
            _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task.Run(ConsumeAsync, CancellationToken.None).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        public void AddCapture(IVisualElementCapture capture)
        {
            if (Volatile.Read(ref _completionState) != 0 || _lifetimeCancellation.IsCancellationRequested || !_captures.Writer.TryWrite(capture))
                capture.Dispose();
        }

        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _completionState, 1, 0) == 0) _captures.Writer.TryComplete();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _completionState, 2, 0) != 0) return;
            _lifetimeCancellation.Cancel();
            _captures.Writer.TryComplete();
            Drain();
        }

        private async Task ConsumeAsync()
        {
            var cancellationToken = _lifetimeCancellation.Token;
            try
            {
                await Dispatcher.UIThread.InvokeAsync(_owner.ArrangeEffectWindows, DispatcherPriority.Render, cancellationToken);
                await foreach (var capture in _captures.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    using (capture)
                    {
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to play visual-context scan captures.");
            }
            finally
            {
                Interlocked.Exchange(ref _completionState, 2);
                Drain();
                _lifetimeCancellation.Dispose();
            }
        }

        private void EmitParticle(PixelRect bounds, VisualEffectImage<SKImage> image)
        {
            foreach (var effectWindow in _owner._effectWindows)
            {
                var sourceCenter = new PixelPoint(bounds.Center.X, bounds.Center.Y);
                var startPoint = effectWindow.ScreenPixelToLocal(sourceCenter);
                var startSize = new Size(
                    Math.Max(16d, bounds.Width / effectWindow.Scale),
                    Math.Max(16d, bounds.Height / effectWindow.Scale));
                effectWindow.AddParticle<ScanVisualElementParticle>(startPoint, null, image, null, startSize);
                _owner.ShowAndRaise(effectWindow);
            }
        }

        private void Drain()
        {
            while (_captures.Reader.TryRead(out var capture)) capture.Dispose();
        }
    }
}