using System.Globalization;
using System.Text;
using Everywhere.Automation.Testing;

namespace Everywhere.Automation.Tests;

public sealed class ScenarioGeneratorTests
{
    [Test]
    public void Generate_WhenScenarioAndSeedRepeat_ProducesEquivalentTree()
    {
        var generator = new VisualScenarioGenerator();

        var first = generator.Generate(CommonScenarios.Chat, 42);
        var second = generator.Generate(CommonScenarios.Chat, 42);

        Assert.That(CreateFingerprint(second.Root), Is.EqualTo(CreateFingerprint(first.Root)));
    }

    [Test]
    public void Generate_WhenSeedChanges_ChangesGeneratedDetails()
    {
        var generator = new VisualScenarioGenerator();

        var first = generator.Generate(CommonScenarios.Chat, 42);
        var second = generator.Generate(CommonScenarios.Chat, 43);

        Assert.That(CreateFingerprint(second.Root), Is.Not.EqualTo(CreateFingerprint(first.Root)));
    }

    [Test]
    public void VirtualList_WhenCreated_DoesNotGenerateUnobservedChildren()
    {
        var generatedItems = 0;
        var scenario = Scenario.Define(
            "lazy-list",
            context => new VirtualList(
                context,
                "items",
                100_000,
                (_, index) =>
                {
                    generatedItems++;
                    return new Text(index.ToString(CultureInfo.InvariantCulture));
                }));
        var root = new VisualScenarioGenerator().Generate(scenario, 1).Root;

        Assert.That(root.ChildCount, Is.EqualTo(100_000));
        Assert.That(generatedItems, Is.Zero);

        var item = root.GetChild(50_000);

        Assert.Multiple(() =>
        {
            Assert.That(item.TextContent, Is.EqualTo("50000"));
            Assert.That(generatedItems, Is.EqualTo(1));
        });
    }

    [Test]
    public void VirtualList_WhenSameIndexIsRequested_RecreatesEquivalentItem()
    {
        var scenario = Scenario.Define(
            "indexed-list",
            context => new VirtualList(
                context,
                "items",
                100_000,
                (itemContext, _) => itemContext.RandomText("content", ScenarioTextKind.Message)));
        var root = new VisualScenarioGenerator().Generate(scenario, 42).Root;

        var first = root.GetChild(50_000);
        var second = root.GetChild(50_000);

        Assert.That(CreateFingerprint(second), Is.EqualTo(CreateFingerprint(first)));
    }

    [Test]
    public void Resolve_WhenMoveNextStepRepeats_ProducesEquivalentState()
    {
        var mutation = new OnMoveNext(step => new Text($"state-{step}"));

        var first = mutation.Resolve(12);
        var second = mutation.Resolve(12);

        Assert.Multiple(() =>
        {
            Assert.That(first.TextContent, Is.EqualTo("state-12"));
            Assert.That(CreateFingerprint(second), Is.EqualTo(CreateFingerprint(first)));
            Assert.That(mutation.Resolve(13).TextContent, Is.EqualTo("state-13"));
        });
    }

    private static string CreateFingerprint(VisualControl root)
    {
        var builder = new StringBuilder();
        Append(root, builder);
        return builder.ToString();

        static void Append(VisualControl control, StringBuilder builder)
        {
            builder.Append((int)control.Kind)
                .Append('|').Append(control.Key)
                .Append('|').Append(control.Name)
                .Append('|').Append(control.TextContent)
                .Append('|').Append((int)control.States)
                .Append('|').Append(control.IsCore)
                .Append('|').Append(control.ChildCount)
                .AppendLine();

            for (var i = 0; i < control.ChildCount; i++)
            {
                Append(control.GetChild(i), builder);
            }
        }
    }
}
