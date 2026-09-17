using Avalonia.Controls.Primitives;
using Everywhere.AI;
using Everywhere.AI.Configurator;

namespace Everywhere.Views;

/// <summary>
/// A control selects <see cref="AssistantConfiguratorType"/> for a given <see cref="Assistant"/>
/// </summary>
public class AssistantConfiguratorSelector : TemplatedControl
{
    public record ConfiguratorModel(
        AssistantConfiguratorType Type,
        IDynamicLocaleKey HeaderKey,
        IDynamicLocaleKey DescriptionKey
    );

    public sealed record OfficialConfiguratorModel(
        AssistantConfiguratorType Type,
        IDynamicLocaleKey HeaderKey,
        IDynamicLocaleKey DescriptionKey
    ) : ConfiguratorModel(Type, HeaderKey, DescriptionKey);

    public IReadOnlyList<ConfiguratorModel> ConfiguratorModels { get; } =
    [
        new OfficialConfiguratorModel(
            AssistantConfiguratorType.Official,
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_OfficialConfiguratorModel_Header),
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_OfficialConfiguratorModel_Description)),
        new(
            AssistantConfiguratorType.PresetBased,
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_PresetBasedConfiguratorModel_Header),
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_PresetBasedConfiguratorModel_Description)),
        new(
            AssistantConfiguratorType.Advanced,
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_AdvancedConfiguratorModel_Header),
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_AdvancedConfiguratorModel_Description)),
    ];

    public static readonly DirectProperty<AssistantConfiguratorSelector, ConfiguratorModel?> SelectedConfiguratorModelProperty =
        AvaloniaProperty.RegisterDirect<AssistantConfiguratorSelector, ConfiguratorModel?>(
            nameof(SelectedConfiguratorModel),
            o => o.SelectedConfiguratorModel,
            (o, v) => o.SelectedConfiguratorModel = v);

    public ConfiguratorModel? SelectedConfiguratorModel
    {
        get;
        set
        {
            if (!SetAndRaise(SelectedConfiguratorModelProperty, ref field, value)) return;
            if (_isAssistantChanging) return;
            if (Assistant is not { } assistant) return;
            if (value is null) return;

            assistant.ConfiguratorType = value.Type;
        }
    }

    public static readonly DirectProperty<AssistantConfiguratorSelector, Assistant?> AssistantProperty =
        AvaloniaProperty.RegisterDirect<AssistantConfiguratorSelector, Assistant?>(
            nameof(Assistant),
            o => o.Assistant,
            (o, v) => o.Assistant = v);

    public Assistant? Assistant
    {
        get;
        set
        {
            _isAssistantChanging = true;
            try
            {
                SetAndRaise(AssistantProperty, ref field, value);
                SelectedConfiguratorModel = ConfiguratorModels
                    .AsValueEnumerable()
                    .FirstOrDefault(m => m.Type == (value?.ConfiguratorType ?? AssistantConfiguratorType.Official));
            }
            finally
            {
                _isAssistantChanging = false;
            }
        }
    }

    /// <summary>
    /// Defines the <see cref="IsSettingsVisible"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsSettingsVisibleProperty =
        AvaloniaProperty.Register<AssistantConfiguratorSelector, bool>(
            nameof(IsSettingsVisible),
            true);

    /// <summary>
    /// Gets or sets a value indicating whether the settings content is visible.
    /// </summary>
    public bool IsSettingsVisible
    {
        get => GetValue(IsSettingsVisibleProperty);
        set => SetValue(IsSettingsVisibleProperty, value);
    }

    private bool _isAssistantChanging;
}