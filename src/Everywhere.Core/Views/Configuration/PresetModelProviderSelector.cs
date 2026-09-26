using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using Everywhere.AI;

namespace Everywhere.Views;

/// <summary>
/// Applies a provider's defaults only for an explicit selection in the editor.
/// Restoring a saved configuration or attaching the control only updates its displayed selection.
/// </summary>
public sealed class PresetModelProviderSelector : ComboBox
{
    public PresetAssistantConfiguration Configuration { get; }

    protected override Type StyleKeyOverride => typeof(ComboBox);

    private readonly AssistantCatalog _catalog;
    private bool _isAttached;
    private bool _isSynchronizing;

    // TODO: maybe use axaml for default template, but this is simpler for now
    public PresetModelProviderSelector(PresetAssistantConfiguration configuration, AssistantCatalog catalog)
    {
        Configuration = configuration;
        _catalog = catalog;
        ItemsSource = PresetModelTemplates.Providers;
        MinWidth = 240;
        MinHeight = 40;
        MaxDropDownHeight = 480;
        HorizontalAlignment = HorizontalAlignment.Left;
        TextSearch.SetTextBinding(this, new Binding(nameof(ModelProviderTemplate.Id)));
        SelectionChanged += HandleSelectionChanged;
        SynchronizeSelection();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource(typeof(ModelProviderTemplate), ActualThemeVariant, out var template) && template is IDataTemplate dataTemplate)
            ItemTemplate = dataTemplate;
        Configuration.PropertyChanged += HandleConfigurationChanged;
        SynchronizeSelection();
        _isAttached = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Configuration.PropertyChanged -= HandleConfigurationChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isAttached || _isSynchronizing || SelectedItem is not ModelProviderTemplate selected || selected.Id == Configuration.ProviderId) return;

        _catalog.ApplyPresetProvider(Configuration, selected);
    }

    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PresetAssistantConfiguration.ProviderId))
        {
            Dispatcher.UIThread.PostOnDemand(SynchronizeSelection);
        }
    }

    private void SynchronizeSelection()
    {
        _isSynchronizing = true;
        try
        {
            SelectedItem = Configuration.ModelProviderTemplate;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }
}