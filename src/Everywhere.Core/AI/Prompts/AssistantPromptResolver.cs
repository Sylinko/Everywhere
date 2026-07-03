using Microsoft.Extensions.Logging;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Resolved system prompt template for an assistant generation request.
/// </summary>
/// <param name="PromptId">
/// Prompt Manager ID used for the template. A null value means the caller supplied a one-off
/// override and no stored prompt reference participated in resolution.
/// </param>
/// <param name="UsedFallback">
/// True when a non-empty prompt reference could not be resolved and the default prompt was used.
/// </param>
public sealed record AssistantPromptResolution(
    Guid? PromptId,
    string Template,
    bool UsedFallback,
    IReadOnlyList<PromptDiagnostic> Diagnostics
);

/// <summary>
/// Resolves the active system prompt template for chat runtime use.
/// </summary>
public interface IAssistantPromptResolver
{
    /// <summary>
    /// Resolves a prompt template, honoring explicit overrides before stored assistant references.
    /// </summary>
    Task<AssistantPromptResolution> ResolveSystemPromptAsync(
        Assistant assistant,
        string? systemPromptOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Prompt Manager based implementation of assistant prompt resolution.
/// </summary>
/// <remarks>
/// The resolver is deliberately tolerant at runtime: a missing stored prompt produces a diagnostic
/// and falls back to the built-in default prompt instead of breaking chat generation.
/// </remarks>
public sealed class AssistantPromptResolver(
    IPromptService promptService,
    ILogger<AssistantPromptResolver> logger
) : IAssistantPromptResolver
{
    /// <inheritdoc />
    public async Task<AssistantPromptResolution> ResolveSystemPromptAsync(
        Assistant assistant,
        string? systemPromptOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (systemPromptOverride is not null)
        {
            return new AssistantPromptResolution(null, systemPromptOverride, false, []);
        }

        var promptId = assistant is CustomAssistant customAssistant ? customAssistant.SystemPromptId : Guid.Empty;

        if (promptId == Guid.Empty)
        {
            return DefaultResolution();
        }

        var prompt = await promptService.GetPromptAsync(promptId, cancellationToken);
        if (prompt is not null)
        {
            return new AssistantPromptResolution(prompt.Id, prompt.Template, false, []);
        }

        var diagnostic = CreateUnresolvedReferenceDiagnostic(promptId);
        logger.LogWarning(
            "Assistant {AssistantId} references missing prompt {PromptId}; falling back to the default system prompt.",
            (assistant as CustomAssistant)?.Id,
            promptId);

        return new AssistantPromptResolution(
            Guid.Empty,
            promptService.DefaultPrompt.Template,
            true,
            [diagnostic]);
    }

    private AssistantPromptResolution DefaultResolution() =>
        new(Guid.Empty, promptService.DefaultPrompt.Template, false, []);

    internal static PromptDiagnostic CreateUnresolvedReferenceDiagnostic(Guid promptId) =>
        new(
            PromptDiagnosticCode.UnresolvedReference,
            PromptDiagnosticSeverity.Warning,
            new FormattedDynamicLocaleKey(
                LocaleKey.PromptDiagnostic_UnresolvedReference,
                new DirectLocaleKey(promptId.ToString("D"))),
            ActionId: "select-default-prompt");
}