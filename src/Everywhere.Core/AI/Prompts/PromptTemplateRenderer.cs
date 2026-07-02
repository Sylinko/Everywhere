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
        IEnumerable<string>? knownPlaceholderNames = null)
    {
        var analysis = PromptTemplateAnalyzer.Analyze(template, knownPlaceholderNames);
        return new PromptTemplateRenderResult(
            Render(template, resolver),
            analysis.Placeholders,
            analysis.Diagnostics);
    }

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

    [GeneratedRegex(PromptTemplateParser.PlaceholderPattern)]
    private static partial Regex PromptTemplateRegex();
}

/// <summary>
/// Combined output for preview flows that need rendered text and template analysis.
/// </summary>
public sealed record PromptTemplateRenderResult(
    string RenderedText,
    IReadOnlyList<PromptPlaceholderToken> Placeholders,
    IReadOnlyList<PromptDiagnostic> Diagnostics
);