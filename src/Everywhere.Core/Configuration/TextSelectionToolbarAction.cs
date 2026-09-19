using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;

namespace Everywhere.Configuration;

/// <summary>
/// One entry in the toolbar's persisted display order. Null overrides retain the registered strategy's defaults.
/// </summary>
public sealed partial class TextSelectionToolbarAction : ObservableObject
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// References a provider-qualified strategy configuration ID, or is null for a custom prompt action.
    /// </summary>
    public string? BuiltInId { get; init; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string? Name { get; set; }

    [ObservableProperty]
    [SettingsSerializedSubtree]
    public partial ColoredIcon? Icon { get; set; }

    [ObservableProperty]
    public partial string? Prompt { get; set; }

    [JsonIgnore]
    public bool IsBuiltIn => BuiltInId is not null;
}
