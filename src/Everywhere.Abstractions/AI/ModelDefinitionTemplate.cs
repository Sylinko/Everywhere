using MessagePack;
using Everywhere.Collections;

namespace Everywhere.AI;

/// <summary>
/// Defines the properties of an AI model.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record ModelDefinitionTemplate : IModelDefinition
{
    [Key(0)]
    public required string ModelId { get; init; }

    [Key(1)]
    public required string? Name { get; init; }

    [Key(3)]
    public required bool SupportsToolCall { get; init; }

    [Key(4)]
    public DateOnly? KnowledgeCutoff { get; init; }

    [Key(5)]
    public DateOnly? ReleaseDate { get; init; }

    [Key(6)]
    public DateOnly? DeprecationDate { get; init; }

    [Key(7)]
    public required Modalities InputModalities { get; init; }

    [Key(8)]
    public required Modalities OutputModalities { get; init; }

    [Key(9)]
    public required int ContextLimit { get; init; }

    [Key(10)]
    public required int OutputLimit { get; init; }

    [Key(11)]
    public ModelSpecializations Specializations { get; init; }

    [Key(12)]
    public string? IconUrl { get; init; }

    [Key(13)]
    public IDynamicLocaleKey? DescriptionKey { get; init; }

    [Key(14)]
    public ModelPricing? Pricing { get; init; }

    // [Key(15)]
    // public bool SupportsTemperature { get; init; }

    /// <summary>
    /// Official Models Only.
    /// </summary>
    [Key(16)]
    public bool IsQuotaLimited { get; init; }

    /// <summary>
    /// User-facing reasoning effort values in increasing order, when the model catalog declares them.
    /// Other reasoning controls such as toggles and token budgets remain protocol-specific.
    /// </summary>
    [Key(17)]
    public ValueArray<string> ReasoningEffortValues { get; init; }

    /// <summary>
    /// Gets or sets the default model in a model provider.
    /// This indicates the best (powerful but economical) model in the provider.
    /// </summary>
    public bool IsDefault { get; init; }

    public override string ToString() => Name ?? ModelId;
}