using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Configurator for the Everywhere official model provider.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class OfficialAssistantConfigurator : AssistantConfigurator
{
    [DynamicResourceKey(LocaleKey.Empty)]
    public SettingsControl<OfficialModelDefinitionForm> ModelDefinitionForm { get; }

    private readonly Assistant _owner;

    /// <summary>
    /// Configurator for the Everywhere official model provider.
    /// </summary>
    public OfficialAssistantConfigurator(Assistant owner)
    {
        _owner = owner;

        ModelDefinitionForm = new SettingsControl<OfficialModelDefinitionForm>(x => new OfficialModelDefinitionForm(x, owner), false);
    }

    public override void Backup()
    {
        Backup(_owner.ModelId);
    }

    public override void Apply()
    {
        _owner.ModelProviderTemplateId = null;
        _owner.Endpoint = null;
        _owner.RequestTimeoutSeconds = 20;

        _owner.ModelId = Restore(_owner.ModelId);
    }

    public override Assistant ResolveAssistant(ModelSpecializations specialization)
    {
        if (_owner.Specializations.HasFlag(specialization))
        {
            // If the current assistant already has the specialization, return it directly.
            return _owner;
        }

        if (ServiceLocator.Resolve<IOfficialModelProvider>().ModelDefinitions.Items.FirstOrDefault(m => m.Specializations.HasFlag(specialization)) is
            { } modelDefinitionTemplate)
        {
            var systemAssistant = new SystemAssistant(specialization)
            {
                ConfiguratorType = AssistantConfiguratorType.Official
            };
            systemAssistant.ApplyTemplate(modelDefinitionTemplate);
            return systemAssistant;
        }

        // Not found, fallback to selected owner
        return _owner;
    }
}