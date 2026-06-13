namespace Everywhere.StrategyEngine.ConditionExpression.Syntax;

/// <summary>
/// Resolves <c>extra.*</c> paths and lazily invokes extra context providers when needed.
/// </summary>
/// <remarks>
/// Pre-collected <see cref="StrategyContext.ExtraContext"/> wins over provider collection. Provider results are
/// cached per resolver instance, which is scoped to one condition evaluation, so short-circuiting naturally
/// avoids unnecessary provider calls.
/// </remarks>
internal sealed class ExtraContextPathResolver(
    ExtraContextProviderPipeline? pipeline,
    IReadOnlyDictionary<string, IReadOnlyList<string>> requiredPaths)
{
    private readonly Dictionary<string, ExtraContextNode?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a path under the public <c>extra</c> DSL root.
    /// </summary>
    /// <example><c>extra.file_manager.selection.items[0].path</c></example>
    public ConditionPathValue Resolve(ConditionPathNode path, ConditionEvaluationContext context)
    {
        if (path.Steps.Count == 0)
        {
            return ConditionPathValue.Missing;
        }

        var publicRoot = $"extra.{path.Steps[0].PublicName}";
        if (!_cache.TryGetValue(publicRoot, out var rootNode))
        {
            rootNode = TryGetPrecollectedExtraRoot(context.StrategyContext, path.Steps[0].PublicName);
            if (rootNode is null && pipeline is not null)
            {
                requiredPaths.TryGetValue(publicRoot, out var paths);
                rootNode = pipeline.Collect(
                    publicRoot,
                    paths ?? [path.PublicPath],
                    context.StrategyContext,
                    context.Source,
                    context.Diagnostics,
                    path.Path);
            }

            _cache[publicRoot] = rootNode;
        }

        if (rootNode is null)
        {
            context.AddDiagnostic(
                StrategyDiagnosticSeverity.Warning,
                "condition.root_unavailable",
                $"Condition root '{publicRoot}' is unavailable.",
                path.Path);
            return ConditionPathValue.Missing;
        }

        object? current = rootNode;
        for (var i = 1; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            if (current is ExtraContextNode node)
            {
                if (!node.Children.TryGetValue(step.PublicName, out var child))
                {
                    context.AddDiagnostic(
                        StrategyDiagnosticSeverity.Warning,
                        "condition.path_missing",
                        $"Extra context path segment '{step.PublicName}' is unavailable.",
                        path.Path);
                    return ConditionPathValue.Missing;
                }

                current = child;
            }
            else
            {
                context.AddDiagnostic(
                    StrategyDiagnosticSeverity.Warning,
                    "condition.path_missing",
                    $"Extra context path segment '{step.PublicName}' is unavailable.",
                    path.Path);
                return ConditionPathValue.Missing;
            }

            foreach (var token in step.IndexOrRangeTokens.AsValueEnumerable())
            {
                var unwrapped = UnwrapExtraNode(current);
                if (unwrapped is null)
                {
                    context.AddDiagnostic(
                        StrategyDiagnosticSeverity.Warning,
                        "condition.path_missing",
                        $"Extra context path segment '{step.PublicName}' is unavailable.",
                        path.Path);
                    return ConditionPathValue.Missing;
                }

                current = ConditionPathResolver.ApplyIndexOrRange(unwrapped, token, path.Path, context);
                if (current is null)
                {
                    return ConditionPathValue.Missing;
                }
            }
        }

        return new ConditionPathValue(true, UnwrapExtraNode(current));
    }

    private static ExtraContextNode? TryGetPrecollectedExtraRoot(StrategyContext context, string rootName)
    {
        if (context.ExtraContext?.Roots.TryGetValue(rootName, out var node) is true)
        {
            return node;
        }

        return null;
    }

    private static object? UnwrapExtraNode(object? value)
    {
        if (value is not ExtraContextNode node)
        {
            return value;
        }

        if (node.HasValue)
        {
            return node.Value;
        }

        return node.Children.Count == 0 ?
            null :
            node.Children.AsValueEnumerable().ToDictionary(child => child.Key, child => UnwrapExtraNode(child.Value), StringComparer.Ordinal);
    }
}
