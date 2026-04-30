using System.Diagnostics.CodeAnalysis;
using Everywhere.VisualContext.TestApp;
using Everywhere.VisualContext.Testing;

namespace Everywhere.Core.Tests.Chat.VisualContext;

public sealed class TestAppItemProjectionTests
{
    [Test]
    public void CreateParts_WhenChatItemIsAContainer_PreservesItsLeafContent()
    {
        var scenario = new VisualScenarioGenerator().Generate(CommonScenarios.Chat, 42);
        var messageList = FindByKey(scenario.Root, "messages");
        var message = messageList.GetChild(0);
        var sender = message.GetChild(0).GetChild(1).TextContent;
        var body = message.GetChild(1).TextContent;

        var parts = TestAppItemProjection.CreateParts(message, static control => control);

        Assert.Multiple(() =>
        {
            Assert.That(parts.Select(static part => part.Text), Does.Contain(sender));
            Assert.That(parts.Select(static part => part.Text), Does.Contain(body));
            Assert.That(parts.Select(static part => part.Text), Does.Contain("Reply"));
            Assert.That(parts.Select(static part => part.Text), Does.Not.Contain(nameof(ScenarioControlKind.HorizontalStack)));
        });
    }

    [Test]
    public void CreateParts_WhenItemExceedsMaximumPartCount_StopsAtTheBound()
    {
        var item = new HorizontalStack(new Text("one"), new Text("two"), new Text("three"));

        var parts = TestAppItemProjection.CreateParts(item, static control => control, maximumPartCount: 2);

        Assert.That(parts.Select(static part => part.Text), Is.EqualTo(new[] { "one", "two" }));
    }

    private static VisualControl FindByKey(VisualControl control, string key) =>
        TryFindByKey(control, key, out var result)
            ? result
            : throw new InvalidOperationException($"Control '{key}' was not found in the fixed scenario skeleton.");

    private static bool TryFindByKey(
        VisualControl control,
        string key,
        [NotNullWhen(true)] out VisualControl? result)
    {
        if (control.Key == key)
        {
            result = control;
            return true;
        }

        if (control is VirtualList)
        {
            result = null;
            return false;
        }

        for (var i = 0; i < control.ChildCount; i++)
        {
            if (TryFindByKey(control.GetChild(i), key, out result))
            {
                return true;
            }
        }

        result = null;
        return false;
    }
}
