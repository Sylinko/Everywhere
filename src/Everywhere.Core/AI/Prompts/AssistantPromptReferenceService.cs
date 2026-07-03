using Everywhere.Common;
using Everywhere.Configuration;
using ZLinq;

namespace Everywhere.AI.Prompts;

/// <summary>
/// A custom assistant reference to a Prompt Manager prompt.
/// </summary>
public sealed record AssistantPromptReference(
    Guid Id,
    string? Name,
    Guid SystemPromptId,
    ColoredIcon? Icon,
    string? Description
);

/// <summary>
/// A custom assistant reference whose prompt row is no longer available.
/// </summary>
/// <remarks>
/// Delete confirmation and Prompt Manager diagnostics can use this result to explain why an
/// assistant will fall back to the default prompt at runtime.
/// </remarks>
public sealed record UnresolvedAssistantPromptReference(
    Guid Id,
    string? Name,
    Guid SystemPromptId,
    PromptDiagnostic Diagnostic
);

/// <summary>
/// Finds assistants that reference Prompt Manager prompts.
/// </summary>
public interface IAssistantPromptReferenceService
{
    /// <summary>
    /// Lists assistants currently pointing at the specified prompt ID.
    /// </summary>
    IReadOnlyList<AssistantPromptReference> ListReferences(Guid promptId);

    /// <summary>
    /// Lists assistant prompt references that cannot be resolved to default or persisted prompts.
    /// </summary>
    Task<IReadOnlyList<UnresolvedAssistantPromptReference>> ListUnresolvedReferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets assistants that point at the specified prompt to the built-in default prompt.
    /// </summary>
    /// <returns>The number of assistant references that were changed.</returns>
    int ResetReferencesToDefault(Guid promptId);
}

/// <summary>
/// Settings-backed reverse reference service for custom assistant prompt IDs.
/// </summary>
public sealed class AssistantPromptReferenceService(Settings settings, IPromptService promptService) : IAssistantPromptReferenceService
{
    /// <inheritdoc />
    public IReadOnlyList<AssistantPromptReference> ListReferences(Guid promptId) =>
        settings.Model.CustomAssistants
            .AsValueEnumerable()
            .Where(assistant => assistant.SystemPromptId == promptId)
            .Select(static assistant => new AssistantPromptReference(
                assistant.Id,
                assistant.Name,
                assistant.SystemPromptId,
                assistant.Icon,
                assistant.Description))
            .ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnresolvedAssistantPromptReference>> ListUnresolvedReferencesAsync(CancellationToken cancellationToken = default)
    {
        var existingPromptIds = (await promptService.ListPromptsAsync(cancellationToken))
            .AsValueEnumerable()
            .Select(static prompt => prompt.Id)
            .ToHashSet();

        return settings.Model.CustomAssistants
            .AsValueEnumerable()
            .Where(assistant => assistant.SystemPromptId != Guid.Empty)
            .Where(assistant => !existingPromptIds.Contains(assistant.SystemPromptId))
            .Select(static assistant => new UnresolvedAssistantPromptReference(
                assistant.Id,
                assistant.Name,
                assistant.SystemPromptId,
                AssistantPromptResolver.CreateUnresolvedReferenceDiagnostic(assistant.SystemPromptId)))
            .ToList();
    }

    /// <inheritdoc />
    public int ResetReferencesToDefault(Guid promptId)
    {
        if (promptId == Guid.Empty)
        {
            return 0;
        }

        var count = 0;
        foreach (var assistant in settings.Model.CustomAssistants)
        {
            if (assistant.SystemPromptId != promptId)
            {
                continue;
            }

            assistant.SystemPromptId = Guid.Empty;
            count++;
        }

        return count;
    }
}
