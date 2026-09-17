using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Edits persisted configuration without reconstructing it from a catalog at startup.
/// </summary>
public abstract class AssistantConfigurator : ObservableValidator, IHaveSettingsItems
{
    [SettingsItemIgnore]
    public abstract SettingsItems SettingsItems { get; }

    // Notify only configuration-backed properties. A blanket notification also rebuilds
    // selection item sources during their own two-way binding writeback.
    internal virtual void NotifyConfigurationChanged(AssistantConfiguration previous, AssistantConfiguration current) { }

    internal virtual void NotifyConfigurationChanged(string? propertyName) { }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    public abstract Assistant ResolveAssistant(ModelSpecializations specialization);
}
