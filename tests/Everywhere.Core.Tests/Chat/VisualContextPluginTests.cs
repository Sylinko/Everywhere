using System.Runtime.CompilerServices;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Chat.Plugins.BuiltIn;

namespace Everywhere.Core.Tests.Chat;

public sealed class VisualContextPluginTests
{
    [Test]
    public void BuildWindowList_WhenWindowIsObserved_PublishesIntegerTargetWithoutExposingNativeHandle()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        using var turn = context.BeginTurn();
        var element = context.GetIdentityMap<string>(StringComparer.Ordinal)
            .GetOrAdd(retention, "window", context, static (identity, owner) => new TestVisualElement(owner, identity));
        var snapshot = new VisualElementSnapshot(
            element.Id,
            VisualElementType.TopLevel,
            VisualElementStates.Focused,
            "Editor",
            null,
            false,
            new PixelRect(10, 20, 800, 600),
            null,
            (nint)0x1234);
        var result = new VisualElementQueryResult(element, snapshot, VisualElementFields.All, VisualElementFields.None, null);

        var prompt = InvokeBuildWindowList(null, context, [result], out var representedWindowCount);
        var rendered = prompt.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(representedWindowCount, Is.EqualTo(1));
            Assert.That(rendered, Does.Contain("<TopLevel id=1"));
            Assert.That(rendered, Does.Contain("name=Editor"));
            Assert.That(rendered, Does.Contain("focused"));
            Assert.That(rendered, Does.Not.Contain("1234"));
            Assert.That(rendered, Does.Not.Contain("handle"));
            Assert.That(context.TryGetTarget(1, out var target), Is.True);
            Assert.That(target, Is.TypeOf<ElementTarget>());
            Assert.That((target as ElementTarget)?.Element, Is.SameAs(element));
        });
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "BuildWindowList")]
    private static extern string InvokeBuildWindowList(
        VisualContextPlugin? klass,
        VisualContext context,
        IReadOnlyList<VisualElementQueryResult> windows,
        out int representedWindowCount);

    private sealed class TestVisualElement(VisualContext context, string id) : VisualElement(context, id)
    {
        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => throw new NotSupportedException();

        protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request) =>
            throw new NotSupportedException();

        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) =>
            Task.FromException<IVisualElementCapture>(new NotSupportedException());

        protected override void ReleaseCore() { }
    }
}
