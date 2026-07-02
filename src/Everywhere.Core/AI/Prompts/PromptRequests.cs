namespace Everywhere.AI.Prompts;

/// <summary>
/// Input for creating a persisted user prompt.
/// </summary>
/// <remarks>
/// <paramref name="Id"/> exists for migration/import paths that need to preserve a stable external
/// identity. Normal creation should leave it empty so <see cref="PromptService"/> can generate a new
/// version 7 GUID.
/// </remarks>
/// <param name="MetadataPayload">
/// Optional opaque payload owned by the authoring experience. It is stored and returned but does not
/// affect runtime rendering.
/// </param>
public sealed record PromptCreateRequest(
    string Template,
    string? Name = null,
    PromptSource Source = PromptSource.Blank,
    byte[]? MetadataPayload = null,
    Guid? Id = null
);

/// <summary>
/// Input for replacing editable fields on an existing user prompt.
/// </summary>
/// <remarks>
/// Prompt updates are full-template replacements. Passing <see cref="PromptConstants.DefaultPromptId"/>
/// to the service is a no-op because the built-in default prompt is virtual and immutable.
/// </remarks>
/// <param name="Source">
/// Optional replacement for source metadata. Leave null when an edit should keep the original source.
/// </param>
public sealed record PromptUpdateRequest(
    string Template,
    string? Name = null,
    byte[]? MetadataPayload = null,
    PromptSource? Source = null
);