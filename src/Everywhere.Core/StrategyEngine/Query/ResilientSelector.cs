using System.Text.RegularExpressions;
using Everywhere.Interop;

namespace Everywhere.StrategyEngine.Query;

/// <summary>
/// A resilient selector that matches visual elements based on stable properties.
/// Designed to tolerate visual tree instability (random IDs, changing indices).
/// </summary>
public sealed class ResilientSelector
{
    /// <summary>
    /// Element types to match (matches if element is any of these types).
    /// Empty means match any type.
    /// </summary>
    public IReadOnlyList<VisualElementType> Types { get; init; } = [];

    /// <summary>
    /// Required states (all specified states must be present).
    /// </summary>
    public VisualElementStates RequiredStates { get; init; } = VisualElementStates.None;

    /// <summary>
    /// Forbidden states (none of these states may be present).
    /// </summary>
    public VisualElementStates ForbiddenStates { get; init; } = VisualElementStates.None;

    /// <summary>
    /// Regex pattern to match against element Name.
    /// </summary>
    public Regex? NamePattern { get; init; }

    /// <summary>
    /// Regex pattern to match against element GetText().
    /// </summary>
    public Regex? TextPattern { get; init; }

    /// <summary>
    /// Process name to match (case-insensitive).
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Maximum text length to retrieve for matching (performance optimization).
    /// </summary>
    public int TextMaxLength { get; init; } = 1000;

    /// <summary>
    /// If true, match any type (Types list is ignored).
    /// </summary>
    public bool MatchAnyType { get; init; }

    /// <summary>
    /// Checks if the given element matches this selector.
    /// </summary>
    public bool Matches(IVisualElement element)
    {
        // Check type
        if (!MatchAnyType && Types.Count > 0 && !Types.Contains(element.Type))
        {
            return false;
        }

        // Check required states
        if (RequiredStates != VisualElementStates.None &&
            (element.States & RequiredStates) != RequiredStates)
        {
            return false;
        }

        // Check forbidden states
        if (ForbiddenStates != VisualElementStates.None &&
            (element.States & ForbiddenStates) != VisualElementStates.None)
        {
            return false;
        }

        // Check name pattern
        if (NamePattern is not null)
        {
            var name = element.Name ?? string.Empty;
            if (!NamePattern.IsMatch(name))
            {
                return false;
            }
        }

        // Check process name
        if (ProcessName is not null)
        {
            var elementProcessName = GetProcessName(element);
            if (elementProcessName is null ||
                !ProcessName.Equals(elementProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Check text pattern (expensive, do last)
        if (TextPattern is not null)
        {
            var text = element.GetText(TextMaxLength) ?? string.Empty;
            if (!TextPattern.IsMatch(text))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetProcessName(IVisualElement element)
    {
        var processId = element.ProcessId;
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            // TODO: platform specific optimizations
            // GetModuleBaseName for Windows
            // proc_pidpath for macOS
            // readlink /proc/[pid]/exe for Linux
            return System.Diagnostics.Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a selector that matches any element.
    /// </summary>
    public static ResilientSelector Any => new() { MatchAnyType = true };

    /// <summary>
    /// Creates a selector for a specific element type.
    /// </summary>
    public static ResilientSelector ForType(VisualElementType type) =>
        new() { Types = [type] };

    /// <summary>
    /// Creates a selector for elements with a specific state.
    /// </summary>
    public static ResilientSelector WithState(VisualElementStates state) =>
        new() { MatchAnyType = true, RequiredStates = state };
}
