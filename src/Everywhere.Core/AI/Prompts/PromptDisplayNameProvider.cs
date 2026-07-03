using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Applies the Prompt Manager display-name fallback rules in one place.
/// </summary>
/// <remarks>
/// Guided prompts can be intentionally unnamed. In that case the saved recipe snapshot gives the UI
/// a stable persona label without making recipe metadata part of runtime rendering.
/// </remarks>
public static class PromptDisplayNameProvider
{
    public static IDynamicLocaleKey GetDisplayNameKey(PromptDefinition prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.Name))
        {
            return new DirectLocaleKey(prompt.Name);
        }

        if (prompt.IsDefault)
        {
            return new DynamicLocaleKey(LocaleKey.PromptPage_DefaultPrompt_DisplayName);
        }

        if (TryGetGuidedPersonaNameKey(prompt, out var personaNameKey))
        {
            return personaNameKey;
        }

        return new DynamicLocaleKey(LocaleKey.PromptPage_UntitledPrompt_DisplayName);
    }

    public static string GetDisplayName(PromptDefinition prompt) =>
        GetDisplayNameKey(prompt).ToString() ?? string.Empty;

    private static bool TryGetGuidedPersonaNameKey(
        PromptDefinition prompt,
        [NotNullWhen(true)] out IDynamicLocaleKey? personaNameKey)
    {
        personaNameKey = null;
        if (prompt.Source != PromptSource.Guided || prompt.MetadataPayload is not { Length: > 0 } payload)
        {
            return false;
        }

        try
        {
            var snapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(payload);
            personaNameKey = PromptRecipeCatalog.GetPersonaNameKey(snapshot.PersonaId);
            return personaNameKey is not null;
        }
        catch
        {
            return false;
        }
    }
}
