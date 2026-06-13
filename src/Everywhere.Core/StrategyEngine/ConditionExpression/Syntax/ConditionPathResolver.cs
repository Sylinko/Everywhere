using Everywhere.StrategyEngine.ConditionExpression.PathBinding;

namespace Everywhere.StrategyEngine.ConditionExpression.Syntax;

/// <summary>
/// Resolves bound non-extra paths against a runtime <see cref="StrategyContext"/>.
/// </summary>
/// <remarks>
/// The resolver executes the path steps produced by binding: root lookup, JSON-backed member access, and
/// index/range operations. Extra context is delegated to <see cref="ExtraContextPathResolver"/> because it can
/// trigger provider collection.
/// </remarks>
internal sealed class ConditionPathResolver(
    IReadOnlyDictionary<string, IStrategyPathRootProvider> roots,
    ExtraContextPathResolver extra)
{
    /// <summary>
    /// Resolves a bound path to a runtime value before operator evaluation.
    /// </summary>
    /// <remarks>
    /// Missing roots or null segments are represented as <see cref="ConditionPathValue.Missing"/> and accompanied
    /// by diagnostics; callers should normally turn that into a null condition result.
    /// </remarks>
    public ConditionPathValue Resolve(ConditionPathNode path, ConditionEvaluationContext context)
    {
        object? current;
        if (path.PublicPath.StartsWith("$item", StringComparison.Ordinal))
        {
            current = context.ItemScope;
            if (current is null)
            {
                context.AddDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "condition.path_missing",
                    "Collection predicate item is unavailable.",
                    path.Path);
                return ConditionPathValue.Missing;
            }
        }
        else
        {
            var rootName = path.PublicPath.Split('.', 2)[0];
            if (rootName == "extra")
            {
                return extra.Resolve(path, context);
            }

            if (!roots.TryGetValue(rootName, out var root))
            {
                context.AddDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "condition.unknown_root",
                    $"Unknown condition root '{rootName}'.",
                    path.Path);
                return ConditionPathValue.Missing;
            }

            current = root.GetValue(context.StrategyContext);
            if (current is null)
            {
                context.AddDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    root.IsDeferred ? "condition.root_unavailable" : "condition.path_missing",
                    root.IsDeferred ?
                        $"Condition root '{root.RootName}' is deferred and unavailable." :
                        $"Condition root '{root.RootName}' is unavailable.",
                    path.Path);
                return ConditionPathValue.Missing;
            }
        }

        foreach (var step in path.Steps.AsValueEnumerable())
        {
            if (step.Accessor is not null)
            {
                try
                {
                    current = step.Accessor.GetValue(current);
                }
                catch (Exception ex)
                {
                    context.AddDiagnostic(
                        StrategyDiagnosticSeverity.Warning,
                        "condition.path_missing",
                        $"Path segment '{step.PublicName}' could not be read.",
                        path.Path,
                        ex);
                    return ConditionPathValue.Missing;
                }
            }

            if (current is null)
            {
                context.AddDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "condition.path_missing",
                    $"Path segment '{step.PublicName}' is unavailable.",
                    path.Path);
                return ConditionPathValue.Missing;
            }

            foreach (var token in step.IndexOrRangeTokens.AsValueEnumerable())
            {
                current = ApplyIndexOrRange(current, token, path.Path, context);
                if (current is null)
                {
                    return ConditionPathValue.Missing;
                }
            }
        }

        return new ConditionPathValue(true, current);
    }

    /// <summary>
    /// Applies one index or range token to a collection value.
    /// </summary>
    /// <remarks>
    /// Index failures return null. Range failures return an empty collection and emit
    /// <c>condition.range_out_of_bounds</c>, matching the DSL runtime spec.
    /// </remarks>
    public static object? ApplyIndexOrRange(
        object value,
        ConditionPathToken token,
        string path,
        ConditionEvaluationContext context)
    {
        if (!ConditionRuntimeValues.TryMaterializeCollection(value, out var items))
        {
            context.AddDiagnostic(
                StrategyDiagnosticSeverity.Warning,
                "condition.collection_required",
                $"Index or range '{token.Text}' requires a collection.",
                path);
            return null;
        }

        if (token.Kind == ConditionPathTokenKind.Range)
        {
            var start = ResolveRangeBound(token.Start, items.Count, defaultValue: 0);
            var end = ResolveRangeBound(token.End, items.Count, defaultValue: items.Count);
            if (start < 0 || end < 0 || start > items.Count || end > items.Count || start > end)
            {
                context.AddDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "condition.range_out_of_bounds",
                    $"Range '{token.Text}' is outside the collection bounds.",
                    path);
                return Array.Empty<object?>();
            }

            return items.Skip(start).Take(end - start).ToArray();
        }

        var index = token.Kind == ConditionPathTokenKind.ReverseIndex ? items.Count - token.Index!.Value
            : token.Index!.Value < 0 ? items.Count + token.Index.Value
            : token.Index.Value;
        if (index < 0 || index >= items.Count)
        {
            context.AddDiagnostic(
                StrategyDiagnosticSeverity.Warning,
                "condition.index_out_of_range",
                $"Index '{token.Text}' is outside the collection bounds.",
                path);
            return null;
        }

        return items[index];
    }

    private static int ResolveRangeBound(ConditionRangeBound? bound, int count, int defaultValue)
    {
        if (bound is null)
        {
            return defaultValue;
        }

        if (bound.IsFromEnd)
        {
            return count - bound.Value;
        }

        return bound.Value < 0 ? count + bound.Value : bound.Value;
    }
}

/// <summary>
/// Runtime value helpers shared by path and operator evaluation.
/// </summary>
internal static class ConditionRuntimeValues
{
    /// <summary>
    /// Materializes a non-string enumerable as an indexable collection.
    /// </summary>
    /// <remarks>
    /// Strings are intentionally excluded even though they are enumerable; DSL string operations treat them as
    /// scalar values.
    /// </remarks>
    public static bool TryMaterializeCollection(object? value, out IReadOnlyList<object?> items)
    {
        if (value is null or string)
        {
            items = [];
            return false;
        }

        if (value is IReadOnlyList<object?> typed)
        {
            items = typed;
            return true;
        }

        if (value is IEnumerable enumerable)
        {
            items = enumerable.AsValueEnumerable().Cast<object?>().ToList();
            return true;
        }

        items = [];
        return false;
    }
}
