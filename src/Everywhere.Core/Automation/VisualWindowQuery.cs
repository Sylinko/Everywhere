using System.Diagnostics;
using Everywhere.ProcessIsolation;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.Prompting.Documents;

namespace Everywhere.Automation;

/// <summary>Builds the bounded Agent-facing list of current top-level windows.</summary>
[InHostProcess(ProcessRole.Automation)]
public static class VisualWindowQuery
{
    /// <summary>Gets the default token budget of the window-list projection.</summary>
    public const int DefaultTokenBudget = 20_000;

    /// <summary>Queries current screens and publishes their top-level windows into the active Context turn.</summary>
    public static VisualQueryResult Build(VisualContext context, IVisualElementBackend backend, int targetTokenBudget = DefaultTokenBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetTokenBudget);

        var windows = new List<VisualElementQueryResult>();
        using var retention = context.CreateRetention();
        var screens = GetScreens(retention, backend, out var hasEnumerationFailure);
        foreach (var screenResult in screens)
        {
            foreach (var (window, failure) in screenResult.Element.CreateEnumerator(
                         VisualElementRelation.Child,
                         new VisualElementQueryRequest(
                             VisualElementFields.Type |
                             VisualElementFields.States |
                             VisualElementFields.Name |
                             VisualElementFields.Bounds |
                             VisualElementFields.ProcessId,
                             0)))
            {
                if (failure is not null)
                {
                    hasEnumerationFailure = true;
                    break;
                }

                var result = window ?? throw new InvalidOperationException("A visual relation item must contain either a result or a failure.");
                if (result.Snapshot.Type != VisualElementType.TopLevel) continue;
                retention.Retain(result.Element);
                windows.Add(result);
            }
        }

        return BuildProjection(context, windows, targetTokenBudget, hasEnumerationFailure);
    }

    private static VisualQueryResult BuildProjection(
        VisualContext context,
        IReadOnlyList<VisualElementQueryResult> windows,
        int targetTokenBudget,
        bool hasEnumerationFailure)
    {
        var selectedIndexes = new HashSet<int>();
        for (var index = 0; index < windows.Count; index++) selectedIndexes.Add(index);

        var hasBudgetOmission = false;
        for (var attempt = 0; attempt < windows.Count + 2; attempt++)
        {
            var publication = context.BeginPublication();
            var targetElements = new Dictionary<PromptCompactElement, int>(ReferenceEqualityComparer.Instance);
            var root = new PromptCompactElement("windows").AttributeNotNullOrEmpty(
                "status",
                (hasEnumerationFailure, hasBudgetOmission) switch
                {
                    (true, true) => "Window enumeration was incomplete and some windows were omitted by the prompt budget",
                    (true, false) => "Window enumeration was incomplete",
                    (false, true) => "Some windows were omitted by the prompt budget",
                    _ => null,
                });
            for (var index = 0; index < windows.Count; index++)
            {
                if (!selectedIndexes.Contains(index)) continue;
                var window = windows[index];
                var id = publication.Add(new ElementTarget { Element = window.Element });
                var element = CreateWindowPromptElement(id, window.Snapshot);
                targetElements.Add(element, index);
                root.Add(element.Atomic().WithPriority(Math.Max(1, 1_000_000 - index)));
            }

            var content = new PromptTokenLimit(targetTokenBudget, root);
            var rendered = new PromptDocument { content }.Render(int.MaxValue);
            var includedNodes = new HashSet<PromptNode>(rendered.IncludedNodes, ReferenceEqualityComparer.Instance);
            var survivingIndexes = new HashSet<int>();
            foreach (var (element, index) in targetElements)
            {
                if (includedNodes.Contains(element)) survivingIndexes.Add(index);
            }

            if (survivingIndexes.Count == selectedIndexes.Count)
            {
                publication.Commit();
                return new VisualQueryResult(rendered.Content, publication.Count);
            }

            selectedIndexes.IntersectWith(survivingIndexes);
            hasBudgetOmission = true;
        }

        throw new InvalidOperationException("Window-list prompt projection did not converge after every monotonic target state was exhausted.");
    }

    private static PromptCompactElement CreateWindowPromptElement(int id, VisualElementSnapshot snapshot)
    {
        var element = new PromptCompactElement("TopLevel")
            .Attribute("id", id)
            .AttributeNotNullOrEmpty("name", snapshot.Name?.SafeSubstring(0, 1_024));
        if (snapshot.Bounds is { } bounds) element.Attribute("box", $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
        if (snapshot.ProcessId is > 0 and var processId)
        {
            element.Attribute("pid", processId);
            try
            {
                using var process = Process.GetProcessById(processId);
                element.AttributeNotNullOrEmpty("process", process.ProcessName);
            }
            catch
            {
                // A process can exit between Automation enumeration and metadata lookup.
            }
        }

        var states = snapshot.States.GetValueOrDefault();
        return element
            .Flag("focused", states.HasFlag(VisualElementStates.Focused))
            .Flag("disabled", states.HasFlag(VisualElementStates.Disabled))
            .Flag("offscreen", states.HasFlag(VisualElementStates.Offscreen));
    }

    private static List<VisualElementQueryResult> GetScreens(
        VisualElementRetention retention,
        IVisualElementBackend backend,
        out bool hasEnumerationFailure)
    {
        hasEnumerationFailure = false;
        var primaryScreen = backend.Query(retention, VisualElementLocator.Default, VisualElementResolution.Screen);
        if (primaryScreen is null) return [];

        var screens = new List<VisualElementQueryResult> { primaryScreen };
        EnumerateScreens(retention, primaryScreen.Element, VisualElementRelation.PreviousSibling, screens, ref hasEnumerationFailure);
        EnumerateScreens(retention, primaryScreen.Element, VisualElementRelation.NextSibling, screens, ref hasEnumerationFailure);
        return screens;
    }

    private static void EnumerateScreens(
        VisualElementRetention retention,
        VisualElement primaryScreen,
        VisualElementRelation relation,
        List<VisualElementQueryResult> screens,
        ref bool hasEnumerationFailure)
    {
        foreach (var (result, failure) in primaryScreen.CreateEnumerator(relation, VisualElementQueryRequest.Default))
        {
            if (failure is not null)
            {
                hasEnumerationFailure = true;
                return;
            }

            var screen = result ?? throw new InvalidOperationException("A visual relation item must contain either a result or a failure.");
            retention.Retain(screen.Element);
            if (relation == VisualElementRelation.PreviousSibling) screens.Insert(0, screen);
            else screens.Add(screen);
        }
    }
}