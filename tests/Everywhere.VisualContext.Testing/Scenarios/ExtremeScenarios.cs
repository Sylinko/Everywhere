namespace Everywhere.VisualContext.Testing;

/// <summary>
/// Provides deliberately adversarial visual-tree shapes used to verify traversal bounds.
/// </summary>
public static class ExtremeScenarios
{
    /// <summary>
    /// Gets every extreme-shape scenario in stable catalog order.
    /// </summary>
    public static IReadOnlyList<Scenario> All =>
    [
        SingleLongText,
        HugeChildCount,
        FragmentedParagraph,
        DeepEmptyContainers,
        MixedPassiveAndInteractive,
        MultipleCoreElements,
        DisconnectedRoots,
        MutatesOnMoveNext,
        EqualPrioritySiblings,
    ];

    /// <summary>
    /// Gets one text element whose content is large enough to require provider-side truncation.
    /// </summary>
    public static Scenario SingleLongText { get; } = Scenario.Define(
        "extreme-single-long-text",
        context => new Window(new Text(CreateLongText(context, 1_000_000)) { IsCore = true }));

    /// <summary>
    /// Gets a virtual list with one hundred thousand lazily generated direct children.
    /// </summary>
    public static Scenario HugeChildCount { get; } = Scenario.Define(
        "extreme-huge-child-count",
        context => new Window(new VirtualList(
            context,
            "items",
            100_000,
            (itemContext, index) => new HorizontalStack(
                new Text(index.ToString()),
                itemContext.RandomText("content", ScenarioTextKind.Message)))));

    /// <summary>
    /// Gets one logical paragraph split into thousands of passive text children.
    /// </summary>
    public static Scenario FragmentedParagraph { get; } = Scenario.Define(
        "extreme-fragmented-paragraph",
        context => new Window(new FragmentedText(CreateLongText(context, 100_000), 10_000) { IsCore = true }));

    /// <summary>
    /// Gets a useful leaf hidden beneath a long run of semantically empty containers.
    /// </summary>
    public static Scenario DeepEmptyContainers { get; } = Scenario.Define(
        "extreme-deep-empty-containers",
        context => new Window(CreateDeepContainers(context.RandomText("anchor", ScenarioTextKind.Sentence), 512)));

    /// <summary>
    /// Gets highly fragmented passive content interleaved with independently actionable descendants.
    /// </summary>
    public static Scenario MixedPassiveAndInteractive { get; } = Scenario.Define(
        "extreme-mixed-passive-interactive",
        context => new Window(new Group(
            new FragmentedText(CreateLongText(context, 20_000), 2_000),
            new Button("Primary action") { IsCore = true },
            new Link("Related details"),
            new TextBox(context.RandomTextValue("input", ScenarioTextKind.Sentence)))));

    /// <summary>
    /// Gets several core controls under a single native root.
    /// </summary>
    public static Scenario MultipleCoreElements { get; } = Scenario.Define(
        "extreme-multiple-core-elements",
        context => new Window(
            new TextBox(context.RandomTextValue("first", ScenarioTextKind.Sentence)) { IsCore = true },
            new Button("Run") { IsCore = true },
            new Link("Documentation") { IsCore = true },
            context.RandomText("background", ScenarioTextKind.Paragraph)));

    /// <summary>
    /// Gets two genuinely disconnected top-level roots without introducing a synthetic common parent.
    /// </summary>
    public static Scenario DisconnectedRoots { get; } = Scenario.DefineRoots(
        "extreme-disconnected-roots",
        context =>
        [
            new Window(
                context.RandomText("primary/title", ScenarioTextKind.Title),
                new Button("Primary action") { IsCore = true }),
            new Window(
                context.RandomText("secondary/title", ScenarioTextKind.Title),
                new TextBox(context.RandomTextValue("secondary/input", ScenarioTextKind.Sentence)) { IsCore = true }),
        ]);

    /// <summary>
    /// Gets a tree whose visible structure and content change deterministically on every MoveNext attempt.
    /// </summary>
    public static Scenario MutatesOnMoveNext { get; } = Scenario.Define(
        "extreme-mutates-on-move-next",
        context => new OnMoveNext(step => new Window(
            new Text($"revision-{step}"),
            new Repeat(
                context.For($"revision-{step}"),
                "items",
                (int)(step % 7) + 1,
                (itemContext, index) => new Button(
                    $"{index}: {itemContext.RandomTextValue("label", ScenarioTextKind.Title)}")))));

    /// <summary>
    /// Gets many structurally equal siblings so traversal tie-breaking can be characterized.
    /// </summary>
    public static Scenario EqualPrioritySiblings { get; } = Scenario.Define(
        "extreme-equal-priority-siblings",
        context => new Window(new Repeat(
            context,
            "siblings",
            256,
            (itemContext, _) => itemContext.RandomText("content", ScenarioTextKind.Sentence))));

    private static string CreateLongText(ScenarioContext context, int minimumLength)
    {
        var paragraph = context.RandomTextValue("long-text", ScenarioTextKind.Paragraph);
        var repetitions = minimumLength / Math.Max(1, paragraph.Length) + 1;
        return string.Concat(Enumerable.Repeat(paragraph + Environment.NewLine, repetitions));
    }

    private static VisualControl CreateDeepContainers(VisualControl leaf, int depth)
    {
        var current = leaf;
        for (var i = depth - 1; i >= 0; i--)
        {
            current = new Panel(current) { Key = $"depth-{i}" };
        }

        return current;
    }
}
