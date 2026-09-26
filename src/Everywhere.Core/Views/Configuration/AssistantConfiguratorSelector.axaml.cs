using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Everywhere.AI;

namespace Everywhere.Views;

/// <summary>
/// Selects a configuration mode and retains one in-memory draft per mode for the control's lifetime.
/// </summary>
public class AssistantConfiguratorSelector : TemplatedControl
{
    public enum ConfigurationMode
    {
        Official,
        Preset,
        Advanced
    }

    public record ConfiguratorModel(
        ConfigurationMode Mode,
        IDynamicLocaleKey HeaderKey,
        IDynamicLocaleKey DescriptionKey
    );

    public sealed record OfficialConfiguratorModel(
        ConfigurationMode Mode,
        IDynamicLocaleKey HeaderKey,
        IDynamicLocaleKey DescriptionKey
    ) : ConfiguratorModel(Mode, HeaderKey, DescriptionKey);

    public IReadOnlyList<ConfiguratorModel> ConfiguratorModels { get; } =
    [
        new OfficialConfiguratorModel(
            ConfigurationMode.Official,
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_OfficialConfiguratorModel_Header),
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_OfficialConfiguratorModel_Description)),
        new(
            ConfigurationMode.Preset,
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_PresetBasedConfiguratorModel_Header),
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_PresetBasedConfiguratorModel_Description)),
        new(
            ConfigurationMode.Advanced,
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_AdvancedConfiguratorModel_Header),
            new DynamicLocaleKey(LocaleKey.AssistantConfiguratorSelector_AdvancedConfiguratorModel_Description)),
    ];

    public static readonly DirectProperty<AssistantConfiguratorSelector, ConfiguratorModel?> SelectedConfiguratorModelProperty =
        AvaloniaProperty.RegisterDirect<AssistantConfiguratorSelector, ConfiguratorModel?>(
            nameof(SelectedConfiguratorModel),
            control => control.SelectedConfiguratorModel,
            (control, value) => control.SelectedConfiguratorModel = value);

    public ConfiguratorModel? SelectedConfiguratorModel
    {
        get;
        set
        {
            if (!SetAndRaise(SelectedConfiguratorModelProperty, ref field, value)) return;
            if (_isSynchronizing || Assistant is not { } assistant || value is null) return;

            SwitchConfiguration(assistant, value.Mode);
        }
    }

    public static readonly DirectProperty<AssistantConfiguratorSelector, Assistant?> AssistantProperty =
        AvaloniaProperty.RegisterDirect<AssistantConfiguratorSelector, Assistant?>(
            nameof(Assistant),
            control => control.Assistant,
            (control, value) => control.Assistant = value);

    public Assistant? Assistant
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;

            _isSynchronizing = true;
            try
            {
                if (field is not null) field.PropertyChanged -= HandleAssistantPropertyChanged;
                _drafts.Clear();
                SetAndRaise(AssistantProperty, ref field, value);
                if (value is not null)
                {
                    value.PropertyChanged += HandleAssistantPropertyChanged;
                    _drafts[GetMode(value.Configuration)] = value.Configuration;
                }

                SelectedConfiguratorModel = ConfiguratorModels
                    .AsValueEnumerable()
                    .FirstOrDefault(model => model.Mode == GetMode(value?.Configuration));
            }
            finally
            {
                _isSynchronizing = false;
            }
        }
    }

    public static readonly StyledProperty<bool> IsSettingsVisibleProperty =
        AvaloniaProperty.Register<AssistantConfiguratorSelector, bool>(nameof(IsSettingsVisible), true);

    public bool IsSettingsVisible
    {
        get => GetValue(IsSettingsVisibleProperty);
        set => SetValue(IsSettingsVisibleProperty, value);
    }

    private readonly Dictionary<ConfigurationMode, AssistantConfiguration> _drafts = [];
    private bool _isSynchronizing;

    private void SwitchConfiguration(Assistant assistant, ConfigurationMode mode)
    {
        var currentMode = GetMode(assistant.Configuration);
        if (currentMode == mode) return;

        _drafts[currentMode] = assistant.Configuration;
        if (!_drafts.TryGetValue(mode, out var configuration))
        {
            configuration = mode switch
            {
                ConfigurationMode.Official => AssistantSnapshotMapper.ToOfficial(assistant.Configuration),
                ConfigurationMode.Preset => AssistantSnapshotMapper.ToPreset(assistant.Configuration),
                _ => AssistantSnapshotMapper.ToAdvanced(assistant.Configuration)
            };
            _drafts.Add(mode, configuration);
        }

        assistant.Configuration = configuration;
    }

    private void HandleAssistantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Assistant.Configuration) || Assistant is not { } assistant) return;

        var mode = GetMode(assistant.Configuration);
        _drafts[mode] = assistant.Configuration;
        var selected = ConfiguratorModels.AsValueEnumerable().First(model => model.Mode == mode);
        if (ReferenceEquals(SelectedConfiguratorModel, selected)) return;

        _isSynchronizing = true;
        try
        {
            SelectedConfiguratorModel = selected;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private static ConfigurationMode GetMode(AssistantConfiguration? configuration) => configuration switch
    {
        PresetAssistantConfiguration => ConfigurationMode.Preset,
        AdvancedAssistantConfiguration => ConfigurationMode.Advanced,
        _ => ConfigurationMode.Official
    };
}