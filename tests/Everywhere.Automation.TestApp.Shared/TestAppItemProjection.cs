using Everywhere.Automation.Testing;

namespace Everywhere.Automation.TestApp;

/// <summary>
/// Projects one logical list item into a bounded sequence of displayable leaf controls.
/// </summary>
/// <remarks>
/// Native virtual grids require a stable column shape and cannot host an arbitrary nested
/// control tree per row. This projection preserves the leaf content and semantic control kind
/// without materializing siblings outside the requested item.
/// </remarks>
public static class TestAppItemProjection
{
    /// <summary>
    /// Gets the default upper bound for native cells created from one logical item.
    /// </summary>
    public const int DefaultMaximumPartCount = 16;

    /// <summary>
    /// Creates a depth-first, bounded projection of the item's displayable leaf controls.
    /// </summary>
    /// <param name="control">The logical item to project.</param>
    /// <param name="resolve">Resolves step-dependent declarations to their current state.</param>
    /// <param name="maximumPartCount">The maximum number of projected native cells.</param>
    public static IReadOnlyList<TestAppDisplayPart> CreateParts(
        VisualControl control,
        Func<VisualControl, VisualControl> resolve,
        int maximumPartCount = DefaultMaximumPartCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPartCount);

        var parts = new List<TestAppDisplayPart>(maximumPartCount);
        Append(control, resolve, parts, maximumPartCount);
        return parts;
    }

    private static void Append(
        VisualControl declaration,
        Func<VisualControl, VisualControl> resolve,
        List<TestAppDisplayPart> parts,
        int maximumPartCount)
    {
        if (parts.Count >= maximumPartCount)
        {
            return;
        }

        var control = resolve(declaration);
        var startingPartCount = parts.Count;
        var text = control.TextContent ?? control.Name;
        if (!string.IsNullOrEmpty(text))
        {
            parts.Add(new TestAppDisplayPart(GetHeader(control), text, control.Kind));
        }

        for (var i = 0; i < control.ChildCount && parts.Count < maximumPartCount; i++)
        {
            Append(control.GetChild(i), resolve, parts, maximumPartCount);
        }

        if (parts.Count == startingPartCount && control.ChildCount == 0)
        {
            parts.Add(new TestAppDisplayPart(control.Kind.ToString(), control.Kind.ToString(), control.Kind));
        }
    }

    private static string GetHeader(VisualControl control) => control.Kind switch
    {
        ScenarioControlKind.Text when control.Key is not null => control.Key,
        ScenarioControlKind.Button or ScenarioControlKind.Link when control.Name is not null => control.Name,
        _ => control.Kind.ToString(),
    };
}

/// <summary>
/// Describes one native cell projected from a logical visual control.
/// </summary>
/// <param name="Header">The suggested column header.</param>
/// <param name="Text">The full display value.</param>
/// <param name="Kind">The semantic kind used to select a native cell type.</param>
public sealed record TestAppDisplayPart(string Header, string Text, ScenarioControlKind Kind);
