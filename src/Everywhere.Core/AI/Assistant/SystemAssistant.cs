using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// SystemAssistant is used for built-in functionalities, e.g., generating title, compress context.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class SystemAssistant(ModelSpecializations requiredSpecializations) : Assistant
{
    [SettingsItemIgnore]
    public ModelSpecializations RequiredSpecializations { get; } = requiredSpecializations;

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool AutoSelect { get; set; } = true;
}