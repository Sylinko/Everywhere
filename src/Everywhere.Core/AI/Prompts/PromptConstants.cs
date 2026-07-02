namespace Everywhere.AI.Prompts;

/// <summary>
/// Stable Prompt Manager constants shared by storage, rendering, diagnostics, and future UI.
/// </summary>
public static class PromptConstants
{
    /// <summary>
    /// Sentinel ID for the virtual built-in default prompt.
    /// </summary>
    /// <remarks>
    /// This value must never be inserted into <c>prompt.db</c>. It is a reference target that lets
    /// assistant settings and UI selections point at the built-in default prompt without creating a
    /// normal prompt row.
    /// </remarks>
    public static Guid DefaultPromptId => Guid.Empty;

    /// <summary>
    /// Placeholder name that expands to <see cref="DefaultPrompts.DefaultSystemPrompt"/>.
    /// </summary>
    public const string DefaultSystemPromptPlaceholder = "DefaultSystemPrompt";

    /// <summary>
    /// Placeholder name owned by runtime chat rendering for skill and tool instructions.
    /// </summary>
    /// <remarks>
    /// Normal user prompts should usually include <c>{DefaultSystemPrompt}</c> instead of referencing
    /// this placeholder directly. Direct use remains valid for advanced prompts that intentionally
    /// bypass the default system prompt.
    /// </remarks>
    public const string SkillsPromptPlaceholder = "SkillsPrompt";
}