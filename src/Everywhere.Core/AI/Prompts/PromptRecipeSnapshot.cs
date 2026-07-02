using MessagePack;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Serialized snapshot of the guided creation choices that produced a prompt.
/// </summary>
/// <remarks>
/// The runtime renderer ignores this type completely. It is stored as opaque MessagePack metadata on
/// a prompt row so the authoring UI can explain or rehydrate guided choices later without making
/// those choices part of the prompt execution contract.
/// </remarks>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class PromptRecipeSnapshot
{
    /// <summary>
    /// Version of this metadata payload, independent of the prompt database schema.
    /// </summary>
    [Key(0)]
    public int SchemaVersion { get; set; }

    [Key(1)]
    public string? PersonaId { get; set; }

    [Key(2)]
    public string? PreferredUserName { get; set; }

    [Key(3)]
    public IReadOnlyList<string> ScenarioIds { get; set; } = [];

    [Key(4)]
    public string? ToneId { get; set; }

    [Key(5)]
    public string? DetailLevelId { get; set; }

    [Key(6)]
    public string? OrganizationId { get; set; }

    [Key(7)]
    public string? AdditionalRequirements { get; set; }

    /// <summary>
    /// True once advanced editing has saved over the generated guided template.
    /// </summary>
    /// <remarks>
    /// Detached snapshots can still support display-name fallback and history, but the guided UI
    /// should not assume it can regenerate the current template losslessly from the stored choices.
    /// </remarks>
    [Key(8)]
    public bool IsDetachedFromRecipe { get; set; }
}
