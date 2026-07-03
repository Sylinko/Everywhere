using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Configurator for the Everywhere official model provider.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class OfficialAssistantConfigurator(Assistant owner) : AssistantConfigurator
{
    [DynamicLocaleKey(LocaleKey.Empty)]
    public SettingsControl<OfficialModelDefinitionForm> ModelDefinitionForm =>
        new(x => new OfficialModelDefinitionForm(x, owner), false);

    public override void Backup()
    {
        Backup(owner.ModelId);
    }

    public override void Apply()
    {
        owner.ModelProviderTemplateId = null;
        owner.Endpoint = null;
        owner.RequestTimeoutSeconds = 20;

        owner.ModelId = Restore(owner.ModelId);
    }

    public override Assistant ResolveAssistant(ModelSpecializations specialization)
    {
        if (specialization == ModelSpecializations.Default || owner.Specializations.HasFlag(specialization))
        {
            // If the current assistant already has the specialization, return it directly.
            return owner;
        }

        var modelDefinitionTemplate = ServiceLocator.Resolve<IOfficialModelProvider>()
            .ModelDefinitions
            .FirstOrDefault(m => m.Specializations.HasFlag(specialization));

        // Not found, fallback to selected owner
        if (modelDefinitionTemplate is null) return owner;

        var systemAssistant = new SystemAssistant(specialization)
        {
            ConfiguratorType = AssistantConfiguratorType.Official
        };
        systemAssistant.ApplyTemplate(modelDefinitionTemplate);
        return systemAssistant;
    }
}
