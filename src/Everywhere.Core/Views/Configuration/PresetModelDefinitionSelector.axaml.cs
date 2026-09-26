using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class PresetModelDefinitionSelector(
    IPresetModelProvider provider,
    AssistantCatalog catalog,
    PresetAssistantConfiguration configuration
) : TemplatedControl, IExceptionHandler
{
    public IPresetModelProvider Provider { get; } = provider;

    public static readonly DirectProperty<PresetModelDefinitionSelector, IReadOnlyList<AssistantCatalogItem>> ItemsProperty =
        AvaloniaProperty.RegisterDirect<PresetModelDefinitionSelector, IReadOnlyList<AssistantCatalogItem>>(
            nameof(Items),
            control => control.Items);

    public IReadOnlyList<AssistantCatalogItem> Items
    {
        get;
        private set => SetAndRaise(ItemsProperty, ref field, value);
    } = [];

    public static readonly DirectProperty<PresetModelDefinitionSelector, AssistantCatalogItem?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<PresetModelDefinitionSelector, AssistantCatalogItem?>(
            nameof(SelectedItem),
            control => control.SelectedItem,
            (control, value) => control.SelectedItem = value);

    public AssistantCatalogItem? SelectedItem
    {
        get => _selectedItem;
        set => SetSelectedItem(value, apply: true);
    }

    public static readonly DirectProperty<PresetModelDefinitionSelector, bool> IsUnlistedProperty =
        AvaloniaProperty.RegisterDirect<PresetModelDefinitionSelector, bool>(nameof(IsUnlisted), control => control.IsUnlisted);

    public bool IsUnlisted
    {
        get;
        private set => SetAndRaise(IsUnlistedProperty, ref field, value);
    }

    private AssistantCatalogItem? _selectedItem;
    private bool _isAttached;
    private bool _isApplyingSelection;
    private bool _isSynchronizing;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Provider.CatalogChanged += HandleCatalogChanged;
        configuration.PropertyChanged += HandleConfigurationChanged;
        Reconcile();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Provider.CatalogChanged -= HandleCatalogChanged;
        configuration.PropertyChanged -= HandleConfigurationChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void Reconcile()
    {
        if (!_isAttached || _isSynchronizing || _isApplyingSelection) return;

        var selection = catalog.Resolve(configuration).PreserveItems(Items);
        _isSynchronizing = true;
        try
        {
            Items = selection.Items;
            IsUnlisted = selection.SelectedItem?.IsFallback == true;
            SetSelectedItem(selection.SelectedItem, apply: false);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SetSelectedItem(AssistantCatalogItem? value, bool apply)
    {
        if (_isSynchronizing && apply || value is null && apply) return;
        if (ReferenceEquals(_selectedItem, value)) return;

        var oldValue = _selectedItem;
        _selectedItem = value;
        RaisePropertyChanged(SelectedItemProperty, oldValue, value);
        if (!apply || value is null) return;

        _isApplyingSelection = true;
        try
        {
            lock (configuration)
            {
                configuration.Apply(value);
            }
        }
        finally
        {
            _isApplyingSelection = false;
        }

        Reconcile();
    }

    private void HandleCatalogChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.PostOnDemand(Reconcile);

    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isApplyingSelection &&
            e.PropertyName is nameof(PresetAssistantConfiguration.ProviderId) or nameof(PresetAssistantConfiguration.ModelId))
            Dispatcher.UIThread.PostOnDemand(Reconcile);
    }

    [RelayCommand]
    private Task RefreshAsync() => Provider.RefreshAsync(this);

    void IExceptionHandler.HandleException(Exception exception, string? message, object? source, int lineNumber)
    {
        if (_isAttached) ToastManager.Error(message ?? LocaleResolver.Common_Error, exception.GetFriendlyMessage());
    }
}