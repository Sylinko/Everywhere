using System.Text.RegularExpressions;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Resolves Prompt Manager placeholders against a caller-provided value resolver.
/// </summary>
/// <remarks>
/// Rendering is recursive by design: if <c>{DefaultSystemPrompt}</c> expands to text containing
/// <c>{Date}</c> or <c>{SkillsPrompt}</c>, those nested placeholders are resolved in the same pass.
/// Unknown placeholders are left literal so advanced or future context-specific placeholders do not
/// disappear silently.
/// </remarks>
/// <example>
/// <code>
/// var text = PromptTemplateRenderer.Render(
///     "{DefaultSystemPrompt}\n\nUse a concise tone.",
///     name => variables.TryGetValue(name, out var value) ? value() : null);
/// </code>
/// </example>
public static partial class PromptTemplateRenderer
{
    /// <summary>
    /// Backstop limit on recursion depth. The path-scoped cycle guard is the primary protection;
    /// this additionally bounds stack/CPU for pathologically deep but acyclic variable chains.
    /// </summary>
    public const int MaxDepth = 16;

    /// <summary>
    /// Renders a template to plain text, leaving unknown placeholders unchanged.
    /// </summary>
    public static string Render(string template, Func<string, string?> resolver) =>
        Expand(template, resolver, new HashSet<string>(StringComparer.Ordinal), 0);

    /// <summary>
    /// Renders a template and returns parse/diagnostic metadata for preview-style consumers.
    /// </summary>
    /// <remarks>
    /// This is a domain-level adapter for future editor and picker previews. It deliberately returns
    /// plain rendered text plus spans and diagnostics, not Avalonia controls or formatted inlines.
    /// </remarks>
    public static PromptTemplateRenderResult RenderWithDiagnostics(
        string template,
        Func<string, string?> resolver,
        IPromptPlaceholderSource? placeholderSource = null,
        PromptPlaceholderContext? placeholderContext = null)
    {
        var analysis = PromptTemplateAnalyzer.Analyze(template, placeholderSource, placeholderContext);
        var segments = RenderSegments(template, resolver);
        return new PromptTemplateRenderResult(
            string.Concat(segments.Select(static segment => segment.Text)),
            analysis.Placeholders,
            analysis.Diagnostics,
            segments);
    }

    /// <summary>
    /// Renders a template using a placeholder source and its explicit runtime context.
    /// </summary>
    public static PromptTemplateRenderResult RenderWithDiagnostics(
        string template,
        IPromptPlaceholderSource placeholderSource,
        PromptPlaceholderContext placeholderContext) =>
        RenderWithDiagnostics(
            template,
            name => placeholderSource.TryResolve(name, placeholderContext, out var value) ? value : null,
            placeholderSource,
            placeholderContext);

    private static string Expand(string text, Func<string, string?> resolver, HashSet<string> visiting, int depth)
    {
        return PromptTemplateRegex().Replace(
            text,
            match =>
            {
                var name = match.Groups[PromptTemplateParser.PlaceholderNameGroup].Value;
                var value = resolver(name);
                if (value is null) return match.Value;
                if (value.IndexOf('{') < 0) return value;
                if (depth >= MaxDepth) return value;
                if (!visiting.Add(name)) return match.Value;

                try
                {
                    return Expand(value, resolver, visiting, depth + 1);
                }
                finally
                {
                    visiting.Remove(name);
                }
            });
    }

    /// <summary>
    /// Renders a template into source-aware text segments for preview highlighting.
    /// </summary>
    /// <remarks>
    /// The concatenated segment text is equivalent to <see cref="Render"/>. Segment metadata records
    /// which placeholder produced each rendered value so UI previews can color filled values with the
    /// same palette used for raw placeholder spans.
    /// </remarks>
    public static IReadOnlyList<PromptTemplateRenderSegment> RenderSegments(
        string template,
        Func<string, string?> resolver)
    {
        var segments = new List<PromptTemplateRenderSegment>();
        ExpandSegments(
            template,
            resolver,
            new HashSet<string>(StringComparer.Ordinal),
            0,
            null,
            segments);
        return MergeAdjacentSegments(segments);
    }

    /// <summary>
    /// Renders source-aware segments using a placeholder source and explicit runtime context.
    /// </summary>
    public static IReadOnlyList<PromptTemplateRenderSegment> RenderSegments(
        string template,
        IPromptPlaceholderSource placeholderSource,
        PromptPlaceholderContext placeholderContext) =>
        RenderSegments(
            template,
            name => placeholderSource.TryResolve(name, placeholderContext, out var value) ? value : null);

    private static void ExpandSegments(
        string text,
        Func<string, string?> resolver,
        HashSet<string> visiting,
        int depth,
        string? inheritedPlaceholderName,
        List<PromptTemplateRenderSegment> segments)
    {
        var cursor = 0;
        foreach (Match match in PromptTemplateRegex().Matches(text))
        {
            if (match.Index > cursor)
            {
                AddLiteralSegment(text[cursor..match.Index], inheritedPlaceholderName, segments);
            }

            var name = match.Groups[PromptTemplateParser.PlaceholderNameGroup].Value;
            var value = resolver(name);
            if (value is null)
            {
                segments.Add(new PromptTemplateRenderSegment(
                    match.Value,
                    name,
                    PromptTemplateRenderSegmentKind.UnresolvedPlaceholder));
            }
            else if (value.IndexOf('{') < 0 || depth >= MaxDepth)
            {
                segments.Add(new PromptTemplateRenderSegment(
                    value,
                    name,
                    PromptTemplateRenderSegmentKind.PlaceholderValue));
            }
            else if (!visiting.Add(name))
            {
                segments.Add(new PromptTemplateRenderSegment(
                    match.Value,
                    name,
                    PromptTemplateRenderSegmentKind.UnresolvedPlaceholder));
            }
            else
            {
                try
                {
                    ExpandSegments(value, resolver, visiting, depth + 1, name, segments);
                }
                finally
                {
                    visiting.Remove(name);
                }
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            AddLiteralSegment(text[cursor..], inheritedPlaceholderName, segments);
        }
    }

    private static void AddLiteralSegment(
        string text,
        string? inheritedPlaceholderName,
        List<PromptTemplateRenderSegment> segments)
    {
        if (text.Length == 0)
        {
            return;
        }

        segments.Add(inheritedPlaceholderName is null ?
            new PromptTemplateRenderSegment(text, null, PromptTemplateRenderSegmentKind.Text) :
            new PromptTemplateRenderSegment(
                text,
                inheritedPlaceholderName,
                PromptTemplateRenderSegmentKind.PlaceholderValue));
    }

    private static IReadOnlyList<PromptTemplateRenderSegment> MergeAdjacentSegments(
        List<PromptTemplateRenderSegment> segments)
    {
        if (segments.Count <= 1)
        {
            return segments;
        }

        var merged = new List<PromptTemplateRenderSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 &&
                merged[^1].PlaceholderName == segment.PlaceholderName &&
                merged[^1].Kind == segment.Kind)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + segment.Text };
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    [GeneratedRegex(PromptTemplateParser.PlaceholderPattern)]
    private static partial Regex PromptTemplateRegex();
}

/// <summary>
/// Combined output for preview flows that need rendered text and template analysis.
/// </summary>
public sealed record PromptTemplateRenderResult(
    string RenderedText,
    IReadOnlyList<PromptPlaceholderToken> Placeholders,
    IReadOnlyList<PromptDiagnostic> Diagnostics,
    IReadOnlyList<PromptTemplateRenderSegment> Segments
);

/// <summary>
/// A rendered preview text segment with optional placeholder source metadata.
/// </summary>
/// <param name="PlaceholderName">
/// Placeholder that produced this text. Null means the segment is literal template text.
/// </param>
public sealed record PromptTemplateRenderSegment(
    string Text,
    string? PlaceholderName,
    PromptTemplateRenderSegmentKind Kind);

public enum PromptTemplateRenderSegmentKind
{
    Text,
    PlaceholderValue,
    UnresolvedPlaceholder
}
