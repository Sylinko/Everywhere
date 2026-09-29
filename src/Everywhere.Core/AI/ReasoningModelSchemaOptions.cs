using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.ValueConverters;
using Riok.Mapperly.Abstractions;

namespace Everywhere.AI;

/// <summary>
/// Base class for protocol options that expose a provider-specific reasoning effort value.
/// It owns the user-defined choices and the selected choice. Catalog choices belong to the model
/// configuration and are supplied when an effective value is resolved.
/// </summary>
public abstract partial class ReasoningModelSchemaOptions : ModelSchemaOptions
{
    /// <summary>
    /// Gets or sets the protocol value entered by the user. A pipe separates multiple ordered choices.
    /// </summary>
    public abstract string? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the user's selection within the resolved choices.
    /// An unavailable selection is retained and falls back only when resolving the effective value.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? SelectedReasoningEffort { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    [MapperIgnore]
    public abstract SettingsItems SettingsItems { get; }

    private SettingsStringItem? _reasoningEffortSettingsItem;

    /// <summary>
    /// Resolves the effective reasoning effort value based on the user's input and the default values.
    /// </summary>
    public string? ResolveReasoningEffort(AssistantConfiguration configuration) =>
        ReasoningEffortResolver.Resolve(ReasoningEffort, SelectedReasoningEffort, configuration.DefaultReasoningEffortValues).EffectiveValue;

    /// <summary>
    /// Records the generated input so its catalog placeholder can be bound by the owning assistant.
    /// </summary>
    protected SettingsStringItem RegisterReasoningEffortSettingsItem(SettingsStringItem item)
    {
        _reasoningEffortSettingsItem = item;
        return item;
    }

    internal void BindReasoningEffortPlaceholder(Assistant assistant)
    {
        if (SettingsItems.Count == 0 || _reasoningEffortSettingsItem is not { } item) return;

        item[!SettingsStringItem.PlaceholderTextProperty] = CompiledBinding.Create(
            (Assistant source) => source.Configuration.DefaultReasoningEffortValues,
            source: assistant,
            converter: CommonConverters.JoinStrings,
            converterParameter: "|");
    }
}