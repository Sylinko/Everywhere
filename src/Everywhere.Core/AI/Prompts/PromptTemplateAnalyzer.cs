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
/// Produces diagnostics from placeholder structure and Prompt Manager composition rules.
/// </summary>
/// <remarks>
/// Analyzer diagnostics are intentionally advisory except for empty templates. Save and assignment
/// flows should decide policy based on <see cref="PromptDiagnosticSeverity"/> and
/// <see cref="PromptDiagnosticCode"/>, not by parsing message text.
/// </remarks>
public static class PromptTemplateAnalyzer
{
    /// <summary>
    /// Placeholder names known to the current runtime renderer and title/strategy flows.
    /// </summary>
    /// <remarks>
    /// Callers can pass a custom known-placeholder set when analyzing a narrower context, such as a
    /// strategy prompt that intentionally supports only <c>{Argument}</c> plus preprocessor values.
    /// </remarks>
    public static IReadOnlySet<string> RuntimePlaceholderNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            PromptConstants.DefaultSystemPromptPlaceholder,
            PromptConstants.SkillsPromptPlaceholder,
            "Argument",
            "Date",
            "OS",
            "SystemLanguage",
            "Time",
            "UserMessage",
            "WorkingDirectory"
        };

    /// <summary>
    /// Parses a template and emits v1 Prompt Manager diagnostics.
    /// </summary>
    /// <param name="template">Prompt template text to inspect.</param>
    /// <param name="knownPlaceholderNames">
    /// Optional exact placeholder-name allowlist. When omitted, <see cref="RuntimePlaceholderNames"/>
    /// is used.
    /// </param>
    public static PromptTemplateAnalysisResult Analyze(string template, IEnumerable<string>? knownPlaceholderNames = null)
    {
        var placeholders = PromptTemplateParser.ParsePlaceholders(template);
        var diagnostics = new List<PromptDiagnostic>();

        if (string.IsNullOrWhiteSpace(template))
        {
            diagnostics.Add(
                new PromptDiagnostic(
                    PromptDiagnosticCode.EmptyTemplate,
                    PromptDiagnosticSeverity.Error,
                    new DirectLocaleKey("Prompt template cannot be empty.")));

            return new PromptTemplateAnalysisResult(placeholders, diagnostics);
        }

        var knownPlaceholders = CreateKnownPlaceholderSet(knownPlaceholderNames);
        diagnostics.AddRange(
            placeholders
                .Where(placeholder => !knownPlaceholders.Contains(placeholder.Name))
                .Select(placeholder => new PromptDiagnostic(
                    PromptDiagnosticCode.UnknownPlaceholder,
                    PromptDiagnosticSeverity.Warning,
                    new DirectLocaleKey($"Unknown prompt placeholder: {{{placeholder.Name}}}."),
                    placeholder.Span)));

        var hasDefaultSystemPrompt = Contains(placeholders, PromptConstants.DefaultSystemPromptPlaceholder);
        var hasSkillsPrompt = Contains(placeholders, PromptConstants.SkillsPromptPlaceholder);
        if (!hasDefaultSystemPrompt)
        {
            diagnostics.Add(
                new PromptDiagnostic(
                    PromptDiagnosticCode.MissingDefaultSystemPrompt,
                    PromptDiagnosticSeverity.Warning,
                    new DirectLocaleKey("Prompt does not include the built-in default system prompt."),
                    ActionId: "insert-default-system-prompt"));
        }

        if (!hasDefaultSystemPrompt && !hasSkillsPrompt)
        {
            diagnostics.Add(
                new PromptDiagnostic(
                    PromptDiagnosticCode.MissingSkillsPrompt,
                    PromptDiagnosticSeverity.Warning,
                    new DirectLocaleKey("Prompt bypasses the default prompt and does not include skill instructions."),
                    ActionId: "insert-skills-prompt"));
        }

        return new PromptTemplateAnalysisResult(placeholders, diagnostics);
    }

    private static HashSet<string> CreateKnownPlaceholderSet(IEnumerable<string>? knownPlaceholderNames) =>
        knownPlaceholderNames is null ?
            new HashSet<string>(RuntimePlaceholderNames, StringComparer.Ordinal) :
            new HashSet<string>(knownPlaceholderNames, StringComparer.Ordinal);

    private static bool Contains(IReadOnlyList<PromptPlaceholderToken> placeholders, string name) =>
        placeholders.AsValueEnumerable().Any(placeholder => StringComparer.Ordinal.Equals(placeholder.Name, name));
}