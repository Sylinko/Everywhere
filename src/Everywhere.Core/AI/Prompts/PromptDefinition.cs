namespace Everywhere.AI.Prompts;

/// <summary>
/// Immutable application-facing view of a prompt.
/// </summary>
/// <remarks>
/// <see cref="PromptDefinition"/> deliberately represents both persisted user prompts and the
/// virtual built-in default prompt. Persistence details stay in <c>PromptEntity</c>; callers can
/// treat the returned definitions uniformly while still checking <see cref="IsBuiltIn"/> or
/// <see cref="IsDefault"/> before allowing edit/delete actions.
/// </remarks>
/// <param name="MetadataPayload">
/// Optional opaque MessagePack payload for authoring metadata such as
/// <see cref="PromptRecipeSnapshot"/>. Runtime rendering must ignore this payload.
/// </param>
/// <param name="IsBuiltIn">
/// True for prompts provided by application code instead of <c>prompt.db</c>.
/// </param>
public sealed record PromptDefinition(
    Guid Id,
    string? Name,
    string Template,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    PromptSource Source = PromptSource.Blank,
    byte[]? MetadataPayload = null,
    bool IsBuiltIn = false
)
{
    /// <summary>
    /// Returns whether this definition is the virtual default prompt reference.
    /// </summary>
    public bool IsDefault => Id == PromptConstants.DefaultPromptId;
}