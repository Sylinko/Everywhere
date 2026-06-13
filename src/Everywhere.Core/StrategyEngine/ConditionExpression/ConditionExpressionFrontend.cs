using System.Diagnostics.CodeAnalysis;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Converts the raw <c>when</c> value loaded from YAML/JSON into the canonical Condition DSL syntax tree.
/// </summary>
/// <remarks>
/// The frontend only validates shape and normalizes dotted/indexed path syntax. It intentionally does not know
/// about roots, JSON schema members, operators, or runtime values; those decisions belong to binding and
/// evaluation.
/// </remarks>
internal static class ConditionExpressionFrontend
{
    /// <summary>
    /// Builds canonical syntax for a strategy condition.
    /// </summary>
    /// <remarks>
    /// Examples such as <c>attachments.files[0].extension.in</c> are expanded into nested mapping nodes so later
    /// phases can bind the same tree shape whether authors used dotted keys or explicit YAML nesting.
    /// </remarks>
    public static ConditionSyntaxNode? Compile(
        object? when,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        if (when is null)
        {
            return null;
        }

        if (when is bool boolean)
        {
            return new ConditionScalarSyntaxNode(boolean, "when");
        }

        if (!IsMap(when) && !IsSequence(when))
        {
            diagnostics.Add(
                CreateDiagnostic(
                    "condition.invalid_yaml_shape",
                    "Strategy condition 'when' must be a bool, mapping, or sequence.",
                    source,
                    "when"));
            return null;
        }

        var before = diagnostics.Count;
        var node = BuildNode(when, source, diagnostics, "when");
        return diagnostics.Skip(before).Any(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error) ? null : node;
    }

    private static ConditionSyntaxNode BuildNode(
        object? value,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string path)
    {
        if (TryAsMap(value, out var map))
        {
            return BuildMapping(map, source, diagnostics, path);
        }

        if (TryAsSequence(value, out var sequence))
        {
            var items = new List<ConditionSyntaxNode>(sequence.Count);
            for (var i = 0; i < sequence.Count; i++)
            {
                items.Add(BuildNode(sequence[i], source, diagnostics, $"{path}[{i}]"));
            }

            return new ConditionSequenceSyntaxNode(items, path);
        }

        return new ConditionScalarSyntaxNode(value, path);
    }

    private static ConditionMappingSyntaxNode BuildMapping(
        IReadOnlyDictionary<string, object?> map,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string path)
    {
        var children = new Dictionary<string, ConditionSyntaxNode>(StringComparer.Ordinal);
        foreach (var (rawKey, value) in map)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                diagnostics.Add(
                    CreateDiagnostic(
                        "condition.invalid_yaml_shape",
                        "Condition mapping contains an empty key.",
                        source,
                        path));
                continue;
            }

            var keyPath = $"{path}.{rawKey}";
            var tokenization = ConditionPathTokenizer.Tokenize(rawKey, keyPath, source, diagnostics);
            if (tokenization is null)
            {
                continue;
            }

            var valueNode = BuildNode(value, source, diagnostics, BuildPath(path, tokenization.CanonicalSegments));
            var nested = WrapSegments(tokenization.CanonicalSegments, valueNode, path);
            MergeInto(children, nested.Children, source, diagnostics, path);
        }

        return new ConditionMappingSyntaxNode(children, path);
    }

    private static string BuildPath(string root, IReadOnlyList<string> segments) =>
        segments.Count == 0 ? root : $"{root}.{string.Join('.', segments)}";

    private static ConditionMappingSyntaxNode WrapSegments(
        IReadOnlyList<string> segments,
        ConditionSyntaxNode leaf,
        string rootPath)
    {
        var current = leaf;
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var path = BuildPath(rootPath, segments.Take(i + 1).ToArray());
            current = new ConditionMappingSyntaxNode(
                new Dictionary<string, ConditionSyntaxNode>(StringComparer.Ordinal)
                {
                    [segments[i]] = current
                },
                path);
        }

        return (ConditionMappingSyntaxNode)current;
    }

    private static void MergeInto(
        Dictionary<string, ConditionSyntaxNode> target,
        IReadOnlyDictionary<string, ConditionSyntaxNode> incoming,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        string path)
    {
        foreach (var (key, incomingNode) in incoming)
        {
            if (!target.TryGetValue(key, out var existingNode))
            {
                target[key] = incomingNode;
                continue;
            }

            if (existingNode is ConditionMappingSyntaxNode existingMap &&
                incomingNode is ConditionMappingSyntaxNode incomingMap)
            {
                var merged = new Dictionary<string, ConditionSyntaxNode>(existingMap.Children, StringComparer.Ordinal);
                MergeInto(merged, incomingMap.Children, source, diagnostics, $"{path}.{key}");
                target[key] = existingMap with { Children = merged };
                continue;
            }

            diagnostics.Add(
                CreateDiagnostic(
                    "condition.dotted_collision",
                    $"Condition key '{key}' collides with another value after dotted-form normalization.",
                    source,
                    $"{path}.{key}"));
        }
    }

    private static bool IsMap(object value) => TryAsMap(value, out _);

    private static bool IsSequence(object value) => TryAsSequence(value, out _);

    private static bool TryAsMap(object? value, [NotNullWhen(true)] out IReadOnlyDictionary<string, object?>? map)
    {
        if (value is IReadOnlyDictionary<string, object?> typed)
        {
            map = typed;
            return true;
        }

        map = null;
        return false;
    }

    private static bool TryAsSequence(object? value, [NotNullWhen(true)] out IReadOnlyList<object?>? sequence)
    {
        if (value is IReadOnlyList<object?> typed)
        {
            sequence = typed;
            return true;
        }

        sequence = null;
        return false;
    }

    private static StrategyDiagnostic CreateDiagnostic(
        string code,
        string message,
        StrategySource source,
        string path) =>
        new()
        {
            Severity = StrategyDiagnosticSeverity.Error,
            Code = code,
            MessageKey = new DirectResourceKey(message),
            Path = path,
            ProviderId = source.ProviderId
        };
}
