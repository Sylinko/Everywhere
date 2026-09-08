using Avalonia.Headless.NUnit;
using Everywhere.Automation;
using Everywhere.Chat;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatContextVisualTargetTurnTests
{
    [AvaloniaTest]
    public void VisualTargetTurn_WhenConversationContinuesAndAdvances_PreservesCurrentGenerationThenRetainsIt()
    {
        using var context = new ChatContext();
        context.AdvanceVisualTargetTurn();
        var firstTarget = new TestTarget();
        var firstPublication = context.VisualContext.BeginPublication();
        var firstId = firstPublication.Add(firstTarget);
        firstPublication.Commit();

        context.EnsureVisualTargetTurn();

        Assert.Multiple(() =>
        {
            Assert.That(context.VisualContext.RetainedTurnCount, Is.Zero);
            Assert.That(context.VisualContext.TargetCount, Is.EqualTo(1));
        });

        context.AdvanceVisualTargetTurn();

        Assert.Multiple(() =>
        {
            Assert.That(context.VisualContext.RetainedTurnCount, Is.EqualTo(1));
            Assert.That(context.VisualContext.TryGetTarget(firstId, out var resolved), Is.True);
            Assert.That(resolved, Is.SameAs(firstTarget));
        });

        context.AdvanceVisualTargetTurn();

        Assert.Multiple(() =>
        {
            Assert.That(context.VisualContext.RetainedTurnCount, Is.EqualTo(2));
            Assert.That(context.VisualContext.TargetCount, Is.EqualTo(1));
        });
    }

    private sealed class TestTarget : VisualTarget { }
}
