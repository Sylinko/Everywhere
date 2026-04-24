using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// SystemAssistant is used for built-in functionalities, e.g., generating title, compress context.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class SystemAssistant(ModelSpecializations requiredSpecializations) : Assistant
{
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool AutoSelect { get; set; } = true;

    public Assistant Resolve(Assistant currentAssistant)
    {
        if (AutoSelect) return currentAssistant.Configurator.ResolveAssistant(requiredSpecializations);
        return this;
    }
}