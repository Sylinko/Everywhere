using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public partial class OfficialModelDefinitionSelector(
    IServiceProvider serviceProvider,
    OfficialAssistantConfiguration configuration
) : TemplatedControl, IExceptionHandler
{
    public static readonly DirectProperty<OfficialModelDefinitionSelector, IReadOnlyList<AssistantCatalogItem>> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionSelector, IReadOnlyList<AssistantCatalogItem>>(
            nameof(ItemsSource),
            control => control.ItemsSource);

    public IReadOnlyList<AssistantCatalogItem> ItemsSource
    {
        get;
        private set => SetAndRaise(ItemsSourceProperty, ref field, value);
    } = [];

    public static readonly DirectProperty<OfficialModelDefinitionSelector, AssistantCatalogItem?> SelectedCatalogItemProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionSelector, AssistantCatalogItem?>(
            nameof(SelectedCatalogItem),
            control => control.SelectedCatalogItem,
            (control, value) => control.SelectedCatalogItem = value);

    public AssistantCatalogItem? SelectedCatalogItem
    {
        get => _selectedCatalogItem;
        set => SetSelectedCatalogItem(value, apply: true);
    }

    public static readonly DirectProperty<OfficialModelDefinitionSelector, ModelDefinitionTemplate?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionSelector, ModelDefinitionTemplate?>(
            nameof(SelectedItem),
            control => control.SelectedItem);

    public ModelDefinitionTemplate? SelectedItem
    {
        get;
        private set => SetAndRaise(SelectedItemProperty, ref field, value);
    }

    public static readonly DirectProperty<OfficialModelDefinitionSelector, bool> IsSelectedModelUnavailableProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionSelector, bool>(
            nameof(IsSelectedModelUnavailable),
            control => control.IsSelectedModelUnavailable);

    public bool IsSelectedModelUnavailable
    {
        get;
        private set => SetAndRaise(IsSelectedModelUnavailableProperty, ref field, value);
    }

    public ICloudClient CloudClient { get; } = serviceProvider.GetRequiredService<ICloudClient>();

    public IOfficialModelProvider OfficialModelProvider { get; } = serviceProvider.GetRequiredService<IOfficialModelProvider>();

    private readonly AssistantCatalog _catalog = serviceProvider.GetRequiredService<AssistantCatalog>();
    private AssistantCatalogItem? _selectedCatalogItem;
    private bool _isAttached;
    private bool _isApplyingSelection;
    private bool _isSynchronizing;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        OfficialModelProvider.CatalogChanged += HandleCatalogChanged;
        configuration.PropertyChanged += HandleConfigurationChanged;
        Reconcile();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        OfficialModelProvider.CatalogChanged -= HandleCatalogChanged;
        configuration.PropertyChanged -= HandleConfigurationChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void Reconcile()
    {
        if (!_isAttached || _isSynchronizing || _isApplyingSelection) return;

        var selection = _catalog.Resolve(configuration).PreserveItems(ItemsSource);
        _isSynchronizing = true;
        try
        {
            ItemsSource = selection.Items;
            IsSelectedModelUnavailable = selection.IsSelectedModelUnavailable;
            SetSelectedCatalogItem(selection.SelectedItem, apply: false);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SetSelectedCatalogItem(AssistantCatalogItem? value, bool apply)
    {
        // ComboBox can briefly push null while ItemsSource is changing. The persisted model id is
        // the source of truth until reconciliation installs the matching row from the new list.
        if (_isSynchronizing && apply || value is null && apply) return;
        if (ReferenceEquals(_selectedCatalogItem, value)) return;

        var oldValue = _selectedCatalogItem;
        _selectedCatalogItem = value;
        RaisePropertyChanged(SelectedCatalogItemProperty, oldValue, value);
        SelectedItem = value?.Model;
        if (!apply || value is null) return;

        _isApplyingSelection = true;
        try
        {
            lock (configuration) configuration.Apply(value);
        }
        finally
        {
            _isApplyingSelection = false;
        }

        Reconcile();
    }

    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isApplyingSelection && e.PropertyName == nameof(AssistantConfiguration.ModelId))
        {
            Dispatcher.UIThread.PostOnDemand(Reconcile);
        }
    }

    private void HandleCatalogChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.PostOnDemand(Reconcile);
    }

    [RelayCommand]
    private Task RefreshAsync() => OfficialModelProvider.RefreshAsync(this);

    void IExceptionHandler.HandleException(Exception exception, string? message, object? source, int lineNumber)
    {
        if (_isAttached) ToastManager.Error(message ?? LocaleResolver.Common_Error, exception.GetFriendlyMessage());
    }
}