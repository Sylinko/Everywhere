using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Data;
using Everywhere.AI.Prompts;
using Everywhere.Configuration;
using Everywhere.Skills;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.AI;

/// <summary>
/// Settings façade that exposes a custom assistant's Prompt Manager reference.
/// </summary>
/// <remarks>
/// The façade deliberately stores no settings data of its own. It lets the generated SettingsItems
/// surface render a prompt selector and read-only preview while the serialized assistant model keeps
/// only <see cref="CustomAssistant.SystemPromptId"/>.
/// </remarks>
[GeneratedSettingsItems]
public sealed partial class AssistantPromptSettings(CustomAssistant owner) : ISettingsControl, IHaveSettingsItems
{
    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["Ghost", "NoHeading"])]
    public SettingsControl<AssistantPromptPreviewControl> InformationForm => new(serviceProvider =>
        new AssistantPromptPreviewControl(
            owner,
            serviceProvider.GetRequiredService<IAssistantPromptResolver>(),
            serviceProvider.GetRequiredService<ISkillPromptProvider>()));

    public Control CreateControl(IServiceProvider serviceProvider) =>
        new PromptComboBox(serviceProvider.GetRequiredService<IPromptService>(), serviceProvider)
        {
            [!PromptComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (CustomAssistant x) => x.SystemPromptId,
                source: owner,
                mode: BindingMode.TwoWay)
        };
}