using ZLinq;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Analysis output for a prompt template before or during rendering.
/// </summary>
public sealed record PromptTemplateAnalysisResult(
    IReadOnlyList<PromptPlaceholderToken> Placeholders,
    IReadOnlyList<PromptDiagnostic> Diagnostics
);

/// <summary>
/// Produces diagnostics from placeholder structure plus the supplied placeholder source.
/// </summary>
public static class PromptTemplateAnalyzer
{
    /// <summary>
    /// Parses a template and emits diagnostics for the supplied placeholder source.
    /// </summary>
    public static PromptTemplateAnalysisResult Analyze(
        string template,
        IPromptPlaceholderSource? placeholderSource = null,
        PromptPlaceholderContext? placeholderContext = null)
    {
        placeholderSource ??= SystemPromptPlaceholderSource.Instance;
        placeholderContext ??= PromptPlaceholderContext.Preview;

        var placeholders = PromptTemplateParser.ParsePlaceholders(template);
        var diagnostics = new List<PromptDiagnostic>();

        if (string.IsNullOrWhiteSpace(template))
        {
            diagnostics.Add(
                new PromptDiagnostic(
                    PromptDiagnosticCode.EmptyTemplate,
                    PromptDiagnosticSeverity.Error,
                    new DynamicLocaleKey(LocaleKey.PromptDiagnostic_EmptyTemplate)));

            return new PromptTemplateAnalysisResult(placeholders, diagnostics);
        }

        diagnostics.AddRange(
            placeholders
                .Where(placeholder => !placeholderSource.IsKnown(placeholder.Name, placeholderContext))
                .Select(placeholder => new PromptDiagnostic(
                    PromptDiagnosticCode.UnknownPlaceholder,
                    PromptDiagnosticSeverity.Warning,
                    new FormattedDynamicLocaleKey(
                        LocaleKey.PromptDiagnostic_UnknownPlaceholder,
                        new DirectLocaleKey(placeholder.RawText)),
                    placeholder.Span)));

        placeholderSource.CollectDiagnostics(
            new PromptTemplateAnalysisContext(template, placeholders, placeholderContext),
            diagnostics);

        return new PromptTemplateAnalysisResult(placeholders, diagnostics);
    }
}
