using System.Text;
using Everywhere.Chat;
using Everywhere.Core.Tests.Chat.VisualContext.Testing;
using Everywhere.Automation.Testing;

namespace Everywhere.Core.Tests.Chat.VisualContext;

public sealed class ScenarioCatalogTests
{
    [TestCaseSource(nameof(CatalogCases))]
    public void Generate_WhenCatalogCaseRepeats_ProducesEquivalentBoundedObservation(Scenario scenario, long seed)
    {
        var generator = new VisualScenarioGenerator();

        var first = generator.Generate(scenario, seed);
        var second = generator.Generate(scenario, seed);

        Assert.That(
            CreateBoundedFingerprint(second.Roots),
            Is.EqualTo(CreateBoundedFingerprint(first.Roots)),
            $"Scenario '{scenario.Name}' with seed {seed} was not reproducible in this revision.");
    }

    [Test]
    public void Generate_WhenExtremeShapesAreCreated_PreservesDeclaredBounds()
    {
        var generator = new VisualScenarioGenerator();
        var longText = generator.Generate(ExtremeScenarios.SingleLongText, 42).Root.GetChild(0);
        var hugeList = generator.Generate(ExtremeScenarios.HugeChildCount, 42).Root.GetChild(0);
        var fragmented = generator.Generate(ExtremeScenarios.FragmentedParagraph, 42).Root.GetChild(0);
        var deep = generator.Generate(ExtremeScenarios.DeepEmptyContainers, 42).Root.GetChild(0);

        var observedDepth = 0;
        while (deep.ChildCount == 1)
        {
            observedDepth++;
            deep = deep.GetChild(0);
        }

        Assert.Multiple(() =>
        {
            Assert.That(longText.TextContent, Has.Length.GreaterThanOrEqualTo(1_000_000));
            Assert.That(hugeList.ChildCount, Is.EqualTo(100_000));
            Assert.That(fragmented.ChildCount, Is.EqualTo(10_000));
            Assert.That(observedDepth, Is.EqualTo(512));
            Assert.That(deep.TextContent, Is.Not.Empty);
            Assert.That(
                generator.Generate(ExtremeScenarios.DisconnectedRoots, 42).Roots,
                Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Build_WhenRootsAreDisconnected_ConsumesBothWithoutSyntheticParent()
    {
        var generated = new VisualScenarioGenerator().Generate(ExtremeScenarios.DisconnectedRoots, 42);
        var backend = new ScenarioMockBackend(generated);
        var builder = new VisualContextBuilder(
            backend.RootElements,
            512,
            0,
            VisualContextDetailLevel.Compact,
            VisualContextTraverseDirections.Child);

        var output = builder.Build(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(output, Is.Not.Empty);
            Assert.That(backend.RootElements, Has.Count.EqualTo(2));
            Assert.That(backend.RootElements[0].Parent, Is.Null);
            Assert.That(backend.RootElements[1].Parent, Is.Null);
            Assert.That(backend.Operations.MoveNextAttemptCount, Is.LessThan(100));
        });
    }

    [TestCaseSource(nameof(CommonScenarioCases))]
    public void Build_WhenCommonScenarioUsesLegacyAdapter_RemainsBounded(Scenario scenario)
    {
        var generated = new VisualScenarioGenerator().Generate(scenario, 42);
        var backend = new ScenarioMockBackend(generated);
        var builder = new VisualContextBuilder(
            [backend.RootElement],
            512,
            0,
            VisualContextDetailLevel.Compact,
            VisualContextTraverseDirections.Child);

        var output = builder.Build(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(output, Is.Not.Empty, $"Scenario '{scenario.Name}', seed 42 produced no output.");
            Assert.That(
                backend.Operations.MoveNextAttemptCount,
                Is.LessThan(1_000),
                $"Scenario '{scenario.Name}', seed 42 exceeded the characterization bound.");
            Assert.That(
                backend.Operations.ElementCreatedCount,
                Is.LessThan(500),
                $"Scenario '{scenario.Name}', seed 42 materialized too many elements.");
        });
    }

    private static IEnumerable<TestCaseData> CatalogCases()
    {
        long[] seeds = [0, 1, 42, 0x5eed];
        foreach (var scenario in CommonScenarios.All.Concat(ExtremeScenarios.All))
        {
            foreach (var seed in seeds)
            {
                yield return new TestCaseData(scenario, seed)
                    .SetName($"Generate_{scenario.Name}_{seed}_IsReproducible");
            }
        }
    }

    private static IEnumerable<TestCaseData> CommonScenarioCases()
    {
        foreach (var scenario in CommonScenarios.All)
        {
            yield return new TestCaseData(scenario).SetName($"Build_{scenario.Name}_RemainsBounded");
        }
    }

    private static string CreateBoundedFingerprint(IReadOnlyList<VisualControl> roots)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < roots.Count; i++)
        {
            builder.Append("root:").Append(i).AppendLine();
            Append(roots[i], builder, 0);
        }

        return builder.ToString();

        static void Append(VisualControl control, StringBuilder builder, int depth)
        {
            builder.Append((int)control.Kind)
                .Append('|').Append(control.Key)
                .Append('|').Append(control.Name)
                .Append('|').Append(control.TextContent)
                .Append('|').Append((int)control.States)
                .Append('|').Append(control.IsCore)
                .Append('|').Append(control.ChildCount)
                .AppendLine();

            if (depth >= 16 || control.ChildCount == 0)
            {
                return;
            }

            var prefixCount = Math.Min(4, control.ChildCount);
            for (var i = 0; i < prefixCount; i++)
            {
                Append(control.GetChild(i), builder, depth + 1);
            }

            if (control.ChildCount > prefixCount)
            {
                Append(control.GetChild(control.ChildCount - 1), builder, depth + 1);
            }
        }
    }
}
