namespace Everywhere.AI.Prompts;

/// <summary>
/// Stable diagnostic identifiers emitted by Prompt Manager analysis.
/// </summary>
/// <remarks>
/// Codes are intended for branching, telemetry, tests, and quick-fix lookup. User-facing text should
/// come from <see cref="PromptDiagnostic.MessageKey"/> rather than from enum names.
/// </remarks>
public enum PromptDiagnosticCode
{
    EmptyTemplate,
    UnknownPlaceholder,
    MissingDefaultSystemPrompt,
    MissingSkillsPrompt,
    UnresolvedReference,
    RecursivePlaceholder
}

/// <summary>
/// Indicates whether a diagnostic blocks saving or is only advisory.
/// </summary>
public enum PromptDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Zero-based text span inside the original prompt template.
/// </summary>
/// <remarks>
/// Spans are stored as start and length rather than line/column so editor integrations can map them
/// directly to document offsets. Consumers that display line/column positions can derive those from
/// the template text.
/// </remarks>
public readonly record struct PromptTextSpan(int Start, int Length);

/// <summary>
/// A localized prompt diagnostic, optionally tied to a span and a quick-fix action.
/// </summary>
/// <remarks>
/// <paramref name="ActionId"/> is a stable command key, not display text. UI layers can map it to
/// buttons such as "insert default prompt" without coupling diagnostics to a specific view model.
/// </remarks>
/// <param name="MessageKey">
/// Localizable diagnostic text. Tests and command routing should use <paramref name="Code"/> instead
/// of parsing this message.
/// </param>
/// <param name="Span">
/// Optional location in the original template. Page-level diagnostics can omit it.
/// </param>
public sealed record PromptDiagnostic(
    PromptDiagnosticCode Code,
    PromptDiagnosticSeverity Severity,
    IDynamicLocaleKey MessageKey,
    PromptTextSpan? Span = null,
    string? ActionId = null
);