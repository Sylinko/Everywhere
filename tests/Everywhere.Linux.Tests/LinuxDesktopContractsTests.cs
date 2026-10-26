using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Linux.Automation;
using Everywhere.Linux.Desktop;
using SkiaSharp;

namespace Everywhere.Linux.Tests;

public sealed class LinuxDesktopContractsTests
{
    [Test]
    public void FrameOutsideClient_WindowAt_UsesFrameWithoutMovingClientOrigin()
    {
        var window = new CompositorWindowInfo(
            "opaque-window",
            42,
            100,
            "fixture",
            "Fixture",
            "test-output",
            new PixelRect(100, 120, 600, 400),
            new PixelRect(95, 95, 610, 430),
            false);
        var desktop = new DesktopSnapshot([], [window]);
        Assert.That(desktop.WindowAt(new PixelPoint(98, 100)), Is.SameAs(window));
        Assert.That(window.ClientRect.Position, Is.EqualTo(new PixelPoint(100, 120)));
        Assert.That(desktop.WindowAt(new PixelPoint(98, 100), 100), Is.Null);
    }

    [Test]
    public void UnavailableFrame_SelectionBounds_FallsBackExplicitlyToClient()
    {
        var window = new CompositorWindowInfo(
            "opaque-window",
            42,
            100,
            "fixture",
            "Fixture",
            "test-output",
            new PixelRect(-100, 20, 600, 400),
            null,
            false);
        Assert.That(window.FrameRect, Is.Null);
        Assert.That(window.SelectionBounds, Is.EqualTo(window.ClientRect));
    }

    [Test]
    public async Task MixedDensityOutputs_Capture_ComposesInGlobalLogicalCoordinatesAndDisposesSources()
    {
        var compositor = new FixtureCompositor();
        var capturer = new FixtureCapturer(compositor);
        using var result = await LinuxScreenCapture.CaptureAsync(
            compositor,
            capturer,
            new PixelRect(-20, 20, 40, 20),
            CancellationToken.None);
        Assert.That(result.Bounds, Is.EqualTo(new PixelRect(-20, 20, 40, 20)));
        Assert.That(result.Size, Is.EqualTo(new PixelSize(80, 40)));
        Assert.That(unchecked((uint)Marshal.ReadInt32(result.Data)), Is.EqualTo(0xffff0000));
        Assert.That(unchecked((uint)Marshal.ReadInt32(result.Data, 79 * 4)), Is.EqualTo(0xff0000ff));
        Assert.That(capturer.CapturedNames, Is.EqualTo(new[] { "left", "right" }));
        Assert.That(capturer.Captures.All(item => item.IsDisposed), Is.True);
    }

    [Test]
    public void ChangedOutputGeometry_Capture_RejectsPixelsAndReleasesSource()
    {
        var compositor = new FixtureCompositor();
        var capturer = new FixtureCapturer(compositor)
        {
            HasChangedGeometry = true,
        };
        Assert.ThrowsAsync<InvalidOperationException>(async () => await LinuxScreenCapture.CaptureAsync(
            compositor, capturer, new PixelRect(-20, 20, 40, 20), CancellationToken.None));
        Assert.That(capturer.Captures.Single().IsDisposed, Is.True);
    }

    [Test]
    public void CancellationBetweenOutputs_Capture_ReleasesSourceAndSkipsNextOutput()
    {
        var compositor = new FixtureCompositor();
        using var cancellation = new CancellationTokenSource();
        var capturer = new FixtureCapturer(compositor)
        {
            AfterCapture = cancellation.Cancel,
        };
        Assert.ThrowsAsync<OperationCanceledException>(async () => await LinuxScreenCapture.CaptureAsync(
            compositor, capturer, new PixelRect(-20, 20, 40, 20), cancellation.Token));
        Assert.That(capturer.CapturedNames, Is.EqualTo(new[] { "left" }));
        Assert.That(capturer.Captures.Single().IsDisposed, Is.True);
    }

    [Test]
    public void OutsideAllOutputs_Capture_DoesNotRequestUnrelatedPixels()
    {
        var compositor = new FixtureCompositor();
        var capturer = new FixtureCapturer(compositor);
        Assert.ThrowsAsync<NotSupportedException>(async () => await LinuxScreenCapture.CaptureAsync(
            compositor, capturer, new PixelRect(500, 500, 20, 20), CancellationToken.None));
        Assert.That(capturer.CapturedNames, Is.Empty);
    }

    private sealed class FixtureCompositor : ICompositorStateProvider
    {
        /// <inheritdoc />
        public string InstanceId => "contract-fixture";

        /// <inheritdoc />
        public WindowStackingOrderKind StackingOrderKind => WindowStackingOrderKind.Compositor;

        public OutputInfo[] Outputs { get; } =
        [
            new("left", new PixelRect(-100, 10, 100, 100), 2),
            new("right", new PixelRect(0, 10, 100, 100), 1),
        ];

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<OutputInfo>> GetOutputsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OutputInfo>>(Outputs);
        }

        /// <inheritdoc />
        public ValueTask<PixelPoint> GetGlobalCursorPositionAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(default(PixelPoint));
        }

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<CompositorWindowInfo>> GetWindowsInStackingOrderAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyList<CompositorWindowInfo>>([]);
        }

        /// <inheritdoc />
        public ValueTask<CompositorWindowInfo?> GetActiveWindowAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<CompositorWindowInfo?>(null);
        }
    }

    private sealed class FixtureCapturer(FixtureCompositor compositor) : ISilentScreenCapturer
    {
        public List<string> CapturedNames { get; } = [];
        public List<TrackedCapture> Captures { get; } = [];
        public bool HasChangedGeometry { get; init; }
        public Action? AfterCapture { get; init; }

        /// <inheritdoc />
        public ValueTask<IVisualElementCapture> CaptureOutputAsync(string outputName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = compositor.Outputs.Single(item => item.Name == outputName);
            var density = LinuxScreenCapture.OutputDensity(output);
            using var bitmap = new SKBitmap((int)(output.Bounds.Width * density), (int)(output.Bounds.Height * density));
            bitmap.Erase(outputName == "left" ? SKColors.Red : SKColors.Blue);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var bounds = HasChangedGeometry ? new PixelRect(900, 900, 100, 100) : output.Bounds;
            var capture = new TrackedCapture(LinuxScreenCapture.Decode(png.ToArray(), bounds));
            CapturedNames.Add(outputName);
            Captures.Add(capture);
            AfterCapture?.Invoke();
            return ValueTask.FromResult<IVisualElementCapture>(capture);
        }
    }

    private sealed class TrackedCapture(IVisualElementCapture inner) : IVisualElementCapture
    {
        /// <inheritdoc />
        public PixelRect Bounds => inner.Bounds;

        /// <inheritdoc />
        public PixelFormat Format => inner.Format;

        /// <inheritdoc />
        public AlphaFormat AlphaFormat => inner.AlphaFormat;

        /// <inheritdoc />
        public nint Data => inner.Data;

        /// <inheritdoc />
        public PixelSize Size => inner.Size;

        /// <inheritdoc />
        public int Stride => inner.Stride;

        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public void Dispose()
        {
            IsDisposed = true;
            inner.Dispose();
        }
    }
}