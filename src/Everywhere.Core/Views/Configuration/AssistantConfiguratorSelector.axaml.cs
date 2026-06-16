using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Everywhere.AI;
using Everywhere.AI.Configurator;

namespace Everywhere.Views;

/// <summary>
/// A control selects <see cref="AssistantConfiguratorType"/> for a given <see cref="Assistant"/>
/// </summary>
[TemplatePart(ListBoxPartName, typeof(ListBox), IsRequired = true)]
public class AssistantConfiguratorSelector : TemplatedControl
{
    private const string ListBoxPartName = "PART_ListBox";

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
                _listBox?.SelectedValue = value?.ConfiguratorType;
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
    private ListBox? _listBox;
    private IDisposable? _listBoxSelectionChangedSubscription;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _listBoxSelectionChangedSubscription?.Dispose();

        _listBox = e.NameScope.Find<ListBox>(ListBoxPartName);
        _listBoxSelectionChangedSubscription = _listBox?.AddDisposableHandler(SelectingItemsControl.SelectionChangedEvent, HandleSelectionChanged);
    }

    private void HandleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Assistant is not { } assistant) return;
        if (_isAssistantChanging) return;

        if (e.RemovedItems is [ConfiguratorModel oldModel, ..])
        {
            assistant.GetConfigurator(oldModel.Type).Backup();
        }

        if (e.AddedItems is [ConfiguratorModel newModel, ..])
        {
            assistant.GetConfigurator(newModel.Type).Apply();
        }
    }
}