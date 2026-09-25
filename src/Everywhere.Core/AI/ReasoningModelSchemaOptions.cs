using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Riok.Mapperly.Abstractions;

namespace Everywhere.AI;

/// <summary>
/// Base class for protocol options that expose a provider-specific reasoning effort value.
/// It owns the user-defined choices, the selected choice, and transient defaults advertised by
/// the active catalog model so every consumer resolves the same effective value.
/// </summary>
public abstract partial class ReasoningModelSchemaOptions : ModelSchemaOptions
{
    /// <summary>
    /// Gets or sets the protocol value entered by the user. A pipe separates multiple ordered choices.
    /// </summary>
    public abstract string? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the user's selection within <see cref="ReasoningEffortValues"/>.
    /// An unavailable selection is retained and falls back only when resolving the effective value.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyPropertyChangedFor(nameof(EffectiveReasoningEffort))]
    public partial string? SelectedReasoningEffort { get; set; }

    /// <summary>
    /// Gets catalog-provided choices for the active model. These values are runtime metadata and are
    /// copied into request snapshots, but are never persisted with assistant options.
    /// </summary>
    [JsonIgnore]
    [SettingsItemIgnore]
    public IReadOnlyList<string>? DefaultReasoningEffortValues
    {
        get;
        set
        {
            var normalized = value is { Count: > 0 } ? value.ToArray() : null;
            if (HaveSameValues(field, normalized)) return;

            field = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningEffortValues));
            OnPropertyChanged(nameof(EffectiveReasoningEffort));
            OnPropertyChanged(nameof(ReasoningEffortPlaceholder));
        }
    }

    /// <summary>
    /// Gets the active ordered choices. User input overrides catalog defaults when it contains at
    /// least one valid entry.
    /// </summary>
    [JsonIgnore]
    [SettingsItemIgnore]
    [MapperIgnore]
    public IReadOnlyList<string> ReasoningEffortValues
    {
        get
        {
            var userValues = ReasoningEffort?.Split(['|', '｜'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return userValues?.Length > 0 ? userValues : DefaultReasoningEffortValues ?? [];
        }
    }

    /// <summary>
    /// Gets the single value sent to the provider. When the saved selection is absent or unavailable,
    /// the middle value is used; an even-sized list chooses the value immediately right of center.
    /// </summary>
    [JsonIgnore]
    [SettingsItemIgnore]
    [MapperIgnore]
    public string? EffectiveReasoningEffort
    {
        get
        {
            var values = ReasoningEffortValues;
            if (SelectedReasoningEffort is { } selected && values.Contains(selected, StringComparer.Ordinal))
                return selected;

            return values.Count == 0 ? null : values[values.Count / 2];
        }
    }

    /// <summary>
    /// Gets the catalog defaults displayed as the input placeholder.
    /// </summary>
    [JsonIgnore]
    [SettingsItemIgnore]
    [MapperIgnore]
    public string? ReasoningEffortPlaceholder => Join(DefaultReasoningEffortValues);

    /// <summary>
    /// Binds the catalog default to the generated settings input without storing presentation state.
    /// </summary>
    protected SettingsStringItem BindReasoningEffortPlaceholder(SettingsStringItem item)
    {
        item[!SettingsStringItem.PlaceholderTextProperty] = CompiledBinding.Create(
            (ReasoningModelSchemaOptions source) => source.ReasoningEffortPlaceholder,
            source: this);
        return item;
    }

    private static string? Join(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? string.Join('|', values) : null;

    private static bool HaveSameValues(IReadOnlyList<string>? current, IReadOnlyList<string>? next) =>
        ReferenceEquals(current, next) ||
        current is not null && next is not null && current.SequenceEqual(next, StringComparer.Ordinal);
}