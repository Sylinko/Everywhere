using System.Collections;
using Avalonia;
using Everywhere.Automation;

namespace Everywhere.Core.Tests.Chat;

public sealed class VisualWindowQueryTests
{
    [Test]
    public void Build_WhenWindowIsObserved_PublishesIntegerTargetWithoutExposingNativeHandle()
    {
        using var context = new VisualContext();
        using var turn = context.BeginTurn();

        var outcome = VisualWindowQuery.Build(context, new TestBackend(context));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.RepresentedTargetCount, Is.EqualTo(1));
            Assert.That(outcome.Content, Is.EqualTo("<windows><TopLevel id=1 name=Editor box=10,20,800,600 focused/></windows>"));
            Assert.That(context.TryGetTarget(1, out var target), Is.True);
            Assert.That(target, Is.TypeOf<ElementTarget>());
        });
    }

    [Test]
    public void Build_WhenEnumerationFailsAfterAWindow_PreservesPartialResultAndReportsStatus()
    {
        using var context = new VisualContext();
        using var turn = context.BeginTurn();

        var outcome = VisualWindowQuery.Build(context, new TestBackend(context, shouldFailAfterWindow: true));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.RepresentedTargetCount, Is.EqualTo(1));
            Assert.That(outcome.Content, Does.Contain("<TopLevel id=1"));
            Assert.That(outcome.Content, Does.Contain("status=\"Window enumeration was incomplete\""));
            Assert.That(context.TryGetTarget(1, out _), Is.True);
        });
    }

    // Provides deterministic enumeration for projection and partial-failure behavior. No platform
    // window provider or native element implementation participates in these tests.
    private sealed class TestBackend(VisualContext context, bool shouldFailAfterWindow = false) : IVisualElementBackend
    {
        public VisualElementQueryResult Query(
            VisualElementRetention retention,
            VisualElementLocator locator,
            VisualElementResolution resolution = VisualElementResolution.Direct,
            VisualElementQueryRequest? request = null)
        {
            var element = context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(
                retention,
                "screen",
                (Context: context, ShouldFailAfterWindow: shouldFailAfterWindow),
                static (identity, state) => new TestVisualElement(
                    identity,
                    "screen",
                    VisualElementType.Screen,
                    state.ShouldFailAfterWindow));
            return element.Query(request ?? VisualElementQueryRequest.Default);
        }

        public void Dispose() { }
    }

    private sealed class TestVisualElement(
        VisualElementIdentity identity,
        string id,
        VisualElementType type,
        bool shouldFailAfterWindow = false) : VisualElement(identity, id)
    {
        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) =>
            new(
                this,
                type == VisualElementType.Screen ?
                    new VisualElementSnapshot(Id, type, VisualElementStates.None, "Screen", null, false, new PixelRect(0, 0, 1920, 1080), null, null) :
                    new VisualElementSnapshot(Id, type, VisualElementStates.Focused, "Editor", null, false, new PixelRect(10, 20, 800, 600), null, (nint)0x1234),
                request.RequestedFields,
                VisualElementFields.None,
                null);

        protected override IVisualElementCursor CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request) =>
            type == VisualElementType.Screen && relation == VisualElementRelation.Child ?
                new WindowEnumerator(Context, request, shouldFailAfterWindow) :
                EmptyVisualElementEnumerator.Shared;

        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) =>
            Task.FromException<IVisualElementCapture>(new NotSupportedException());

        protected override void ReleaseCore() { }
    }

    private sealed class WindowEnumerator(VisualContext context, VisualElementQueryRequest request, bool shouldFailAfterWindow) : IVisualElementCursor
    {
        public VisualElementQueryResult Current => _current ?? throw new InvalidOperationException("The enumerator has no current item.");
        object IEnumerator.Current => Current;
        public int Count => 1;
        public int Index => _current is null ? -1 : 0;
        private readonly VisualElementRetention _retention = context.CreateRetention();
        private VisualElementQueryResult? _current;
        private bool _isComplete;
        private bool _hasFailed;

        public bool MoveNext()
        {
            if (_isComplete)
            {
                if (shouldFailAfterWindow && !_hasFailed)
                {
                    _hasFailed = true;
                    throw new TimeoutException("The test window provider timed out.");
                }

                _current = null;
                return false;
            }

            var element = context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(
                _retention,
                "window",
                context,
                static (identity, _) => new TestVisualElement(identity, "window", VisualElementType.TopLevel));
            _current = element.Query(request);
            _isComplete = true;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            _current = null;
            _retention.Dispose();
        }
    }
}
