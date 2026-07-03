namespace Everywhere.AI.Prompts;

/// <summary>
/// Provides the virtual built-in default prompt as a normal prompt definition.
/// </summary>
public interface IDefaultPromptProvider
{
    PromptDefinition DefaultPrompt { get; }
}

/// <summary>
/// Creates the Prompt Manager representation of <see cref="DefaultPrompts.DefaultSystemPrompt"/>.
/// </summary>
/// <remarks>
/// This provider is intentionally storage-free. The default prompt must remain visible to callers
/// without being inserted into <c>prompt.db</c>, synchronized, edited, or deleted as a user prompt.
/// </remarks>
public sealed class DefaultPromptProvider : IDefaultPromptProvider
{
    public PromptDefinition DefaultPrompt =>
        new(
            Guid.Empty,
            null,
            DefaultPrompts.DefaultSystemPrompt,
            IsBuiltIn: true);
}