using System.Collections.Immutable;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Names reserved by the DSL grammar and therefore hidden from schema property binding.
/// </summary>
/// <remarks>
/// A CLR/JSON property named <c>any</c> or <c>count</c> would be ambiguous in paths such as
/// <c>attachments.files.any.extension.in</c>, so JSON metadata accessors filter these names out.
/// </remarks>
internal static class ConditionExpressionKeywords
{
    /// <summary>
    /// Author-facing operator and logical names that cannot be selected as properties.
    /// </summary>
    private static readonly ImmutableHashSet<string> Reserved = ImmutableHashSet.Create<string>(
        StringComparer.Ordinal,
        "all",
        "any",
        "none",
        "not",
        "equals",
        "in",
        "contains",
        "containsAny",
        "containsAll",
        "startsWith",
        "endsWith",
        "regex",
        "glob",
        "caseSensitive",
        "length",
        "min",
        "max",
        "count");

    public static bool IsReserved(string name) => Reserved.Contains(name);
}