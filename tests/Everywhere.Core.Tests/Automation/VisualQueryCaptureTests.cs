using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Views;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Automation;

/// <summary>Verifies query-to-image ownership without initializing a desktop renderer.</summary>
public sealed class VisualQueryCaptureTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task BuildAsync_WhenCaptureDeliveryIsOptional_PreservesPublicationAndOwnership(bool hasReceiver, bool shouldReject)
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        using var turn = context.BeginTurn();
        var capture = new TestCapture();
        var element = context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(retention, "window", context, (_, owner) => new CaptureElement(owner, capture));
        var received = new List<IVisualElementCapture>();
        Action<IVisualElementCapture>? receiver = hasReceiver ? image =>
        {
            if (shouldReject) throw new InvalidOperationException("Delivery failed before ownership transfer.");
            received.Add(image);
        } : null;
        var query = new VisualQuery(context, receiver);
        var result = await query.BuildAsync([element, element], VisualContextPromptOptions.Default, directions: VisualContextTraverseDirections.Core);
        Assert.Multiple(() =>
        {
            Assert.That(element.CaptureCount, Is.EqualTo(hasReceiver ? 1 : 0));
            Assert.That(result.RepresentedTargetCount, Is.EqualTo(1));
            Assert.That(result.Content, Does.Contain("TopLevel"));
            Assert.That(capture.DisposeCount, Is.EqualTo(shouldReject ? 1 : 0));
        });
        context.Dispose();
        foreach (var image in received)
        {
            Assert.That(capture.DisposeCount, Is.Zero);
            image.Dispose();
        }
    }

    [Test]
    public void BuildAsync_WhenCanceledAfterCapture_DisposesUndeliveredImageWithoutPublishing()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        using var turn = context.BeginTurn();
        using var cancellation = new CancellationTokenSource();
        var capture = new TestCapture();
        var element = context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(retention, "window", context, (_, owner) => new CaptureElement(owner, capture, cancellation));
        var query = new VisualQuery(context, _ => Assert.Fail("Canceled capture must not be delivered."));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await query.BuildAsync([element], VisualContextPromptOptions.Default, directions: VisualContextTraverseDirections.Core, cancellationToken: cancellation.Token));
        Assert.Multiple(() =>
        {
            Assert.That(capture.DisposeCount, Is.EqualTo(1));
            Assert.That(context.TargetCount, Is.Zero);
        });
    }

    [Test]
    public void ScanScope_WhenFullOrAbandoned_DisposesEveryOwnedCaptureOnce()
    {
        var effect = new VisualElementEffect(Substitute.For<IVisualElementAnimationTarget>(), NullLogger<VisualElementEffect>.Instance);
        var images = Enumerable.Range(0, 6).Select(_ => new TestCapture()).ToArray();
        using (var scope = effect.CreateScanEffect(CancellationToken.None))
        {
            foreach (var capture in images) scope.AddCapture(capture);
            Assert.That(images.Count(image => image.DisposeCount > 0), Is.EqualTo(2));
        }
        Assert.That(images.All(image => image.DisposeCount == 1), Is.True);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var canceledScope = effect.CreateScanEffect(cancellation.Token);
        var rejected = new TestCapture();
        canceledScope.AddCapture(rejected);
        Assert.That(rejected.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void Image_WhenSharedByScreens_DisposesOnlyAfterLastReference()
    {
        var resource = new TestCapture();
        var image = new VisualEffectImage<TestCapture>(resource);
        image.AddRef();
        image.AddRef();
        image.Dispose();
        image.Dispose();
        Assert.That(resource.DisposeCount, Is.Zero);
        image.Dispose();
        Assert.That(resource.DisposeCount, Is.EqualTo(1));
    }

    private sealed class CaptureElement(VisualContext context, TestCapture capture, CancellationTokenSource? cancellation = null) : VisualElement(context, "window")
    {
        public int CaptureCount { get; private set; }
        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => new(this,
            new VisualElementSnapshot(Id, VisualElementType.TopLevel, VisualElementStates.None, "Window", null, false, new PixelRect(0, 0, 100, 100), null, null), VisualElementFields.All, VisualElementFields.None, null);
        protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters) => new(string.Empty, null, null);
        protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request) => Substitute.For<IVisualElementEnumerator>();
        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            cancellation?.Cancel();
            return Task.FromResult<IVisualElementCapture>(capture);
        }
        protected override void ReleaseCore() { }
    }

    private sealed class TestCapture : IVisualElementCapture
    {
        public PixelRect Bounds => new(-10, 20, 100, 100);
        public PixelSize Size => new(10, 10);
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Opaque;
        public nint Data => 0;
        public int Stride => 40;
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
