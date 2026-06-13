using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Tokenizes one public DSL path key into property, index, reverse-index, and range tokens.
/// </summary>
/// <remarks>
/// The tokenizer is syntax-only. It can accept <c>files[^1]</c> or <c>files[1..^1]</c>, but whether the target is
/// actually a collection is decided later by the binder.
/// </remarks>
internal static class ConditionPathTokenizer
{
    /// <summary>
    /// Parses an author-written key and returns both canonical segments and executable path tokens.
    /// </summary>
    /// <example>
    /// <c>attachments.files[^1].extension</c> produces three canonical segments, with the reverse index attached
    /// to the <c>files</c> segment.
    /// </example>
    public static ConditionPathTokenization? Tokenize(
        string key,
        string diagnosticPath,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        var rawSegments = SplitSegments(key, diagnosticPath, source, diagnostics);
        if (rawSegments is null)
        {
            return null;
        }

        var canonicalSegments = new List<string>(rawSegments.Count);
        var tokens = new List<ConditionPathToken>();
        foreach (var rawSegment in rawSegments)
        {
            if (!TryTokenizeSegment(rawSegment, diagnosticPath, source, diagnostics, out var canonicalSegment, out var segmentTokens))
            {
                return null;
            }

            canonicalSegments.Add(canonicalSegment);
            tokens.AddRange(segmentTokens);
        }

        return new ConditionPathTokenization(canonicalSegments, tokens);
    }

    private static List<string>? SplitSegments(
        string key,
        string diagnosticPath,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics)
    {
        var segments = new List<string>();
        var start = 0;
        var bracketDepth = 0;
        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            switch (ch)
            {
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    if (bracketDepth < 0)
                    {
                        AddInvalidShape(diagnostics, source, diagnosticPath, $"Condition path '{key}' has an unmatched ']'.");
                        return null;
                    }

                    break;
                case '.' when bracketDepth == 0:
                    if (!AddSegment(key[start..i], key, diagnosticPath, source, diagnostics, segments))
                    {
                        return null;
                    }

                    start = i + 1;
                    break;
            }
        }

        if (bracketDepth != 0)
        {
            AddInvalidShape(diagnostics, source, diagnosticPath, $"Condition path '{key}' has an unmatched '['.");
            return null;
        }

        return AddSegment(key[start..], key, diagnosticPath, source, diagnostics, segments) ? segments : null;
    }

    private static bool AddSegment(
        string segment,
        string key,
        string diagnosticPath,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        List<string> segments)
    {
        if (!string.IsNullOrWhiteSpace(segment))
        {
            segments.Add(segment.Trim());
            return true;
        }

        AddInvalidShape(diagnostics, source, diagnosticPath, $"Condition path '{key}' contains an empty segment.");
        return false;
    }

    private static bool TryTokenizeSegment(
        string segment,
        string diagnosticPath,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        [NotNullWhen(true)] out string? canonicalSegment,
        [NotNullWhen(true)] out IReadOnlyList<ConditionPathToken>? tokens)
    {
        canonicalSegment = null;
        tokens = null;

        var result = new List<ConditionPathToken>();
        var bracketStart = segment.IndexOf('[', StringComparison.Ordinal);
        var propertyName = bracketStart < 0 ? segment : segment[..bracketStart];
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            AddInvalidShape(diagnostics, source, diagnosticPath, $"Condition path segment '{segment}' is missing a property name.");
            return false;
        }

        propertyName = propertyName.Trim();
        result.Add(new ConditionPathToken
        {
            Kind = ConditionPathTokenKind.Property,
            Name = propertyName,
            Text = propertyName
        });

        var canonical = propertyName;
        var offset = bracketStart < 0 ? segment.Length : bracketStart;
        while (offset < segment.Length)
        {
            if (segment[offset] != '[')
            {
                AddInvalidShape(diagnostics, source, diagnosticPath, $"Condition path segment '{segment}' contains invalid index syntax.");
                return false;
            }

            var end = segment.IndexOf(']', offset + 1);
            if (end < 0)
            {
                AddInvalidShape(diagnostics, source, diagnosticPath, $"Condition path segment '{segment}' has an unterminated index or range.");
                return false;
            }

            var content = segment[(offset + 1)..end].Trim();
            if (!TryParseIndexOrRange(content, diagnosticPath, source, diagnostics, out var token))
            {
                return false;
            }

            result.Add(token);
            canonical += token.Text;
            offset = end + 1;
        }

        canonicalSegment = canonical;
        tokens = result;
        return true;
    }

    private static bool TryParseIndexOrRange(
        string content,
        string diagnosticPath,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        [NotNullWhen(true)] out ConditionPathToken? token)
    {
        token = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            AddInvalidShape(diagnostics, source, diagnosticPath, "Condition path contains an empty index or range.");
            return false;
        }

        var rangeIndex = content.IndexOf("..", StringComparison.Ordinal);
        if (rangeIndex >= 0)
        {
            if (content.IndexOf("..", rangeIndex + 2, StringComparison.Ordinal) >= 0)
            {
                AddInvalidShape(diagnostics, source, diagnosticPath, $"Range '[{content}]' contains more than one '..'.");
                return false;
            }

            var startText = content[..rangeIndex].Trim();
            var endText = content[(rangeIndex + 2)..].Trim();
            if (!TryParseRangeBound(startText, diagnosticPath, source, diagnostics, allowEmpty: true, out var start) ||
                !TryParseRangeBound(endText, diagnosticPath, source, diagnostics, allowEmpty: true, out var end))
            {
                return false;
            }

            token = new ConditionPathToken
            {
                Kind = ConditionPathTokenKind.Range,
                Start = start,
                End = end,
                Text = $"[{FormatRangeBound(start)}..{FormatRangeBound(end)}]"
            };
            return true;
        }

        if (content.StartsWith('^'))
        {
            if (!TryParsePositiveInt(content[1..], out var fromEnd))
            {
                AddInvalidShape(diagnostics, source, diagnosticPath, $"Reverse index '[{content}]' must use a positive integer.");
                return false;
            }

            token = new ConditionPathToken
            {
                Kind = ConditionPathTokenKind.ReverseIndex,
                Index = fromEnd,
                Text = $"[^{fromEnd.ToString(CultureInfo.InvariantCulture)}]"
            };
            return true;
        }

        if (!int.TryParse(content, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            AddInvalidShape(diagnostics, source, diagnosticPath, $"Index '[{content}]' must be an integer, reverse index, or range.");
            return false;
        }

        token = new ConditionPathToken
        {
            Kind = ConditionPathTokenKind.Index,
            Index = index,
            Text = $"[{index.ToString(CultureInfo.InvariantCulture)}]"
        };
        return true;
    }

    private static bool TryParseRangeBound(
        string text,
        string diagnosticPath,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics,
        bool allowEmpty,
        [NotNullWhen(true)] out ConditionRangeBound? bound)
    {
        bound = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty;
        }

        if (text.StartsWith('^'))
        {
            if (!TryParsePositiveInt(text[1..], out var fromEnd))
            {
                AddInvalidShape(diagnostics, source, diagnosticPath, $"Range bound '{text}' must use a positive integer after '^'.");
                return false;
            }

            bound = new ConditionRangeBound
            {
                Value = fromEnd,
                IsFromEnd = true,
                Text = $"^{fromEnd.ToString(CultureInfo.InvariantCulture)}"
            };
            return true;
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            AddInvalidShape(diagnostics, source, diagnosticPath, $"Range bound '{text}' must be an integer or reverse index.");
            return false;
        }

        bound = new ConditionRangeBound
        {
            Value = index,
            Text = index.ToString(CultureInfo.InvariantCulture)
        };
        return true;
    }

    private static bool TryParsePositiveInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string FormatRangeBound(ConditionRangeBound? bound) => bound?.Text ?? string.Empty;

    private static void AddInvalidShape(
        List<StrategyDiagnostic> diagnostics,
        StrategySource source,
        string path,
        string message) =>
        diagnostics.Add(new StrategyDiagnostic
        {
            Severity = StrategyDiagnosticSeverity.Error,
            Code = "condition.invalid_yaml_shape",
            MessageKey = new DirectResourceKey(message),
            Path = path,
            ProviderId = source.ProviderId
        });
}
