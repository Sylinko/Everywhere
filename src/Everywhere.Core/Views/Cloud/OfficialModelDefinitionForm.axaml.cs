using System.Collections.Specialized;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public partial class OfficialModelDefinitionForm(IServiceProvider serviceProvider, Assistant assistant) : TemplatedControl, IExceptionHandler
{
    public sealed class ItemWrapper(ModelDefinitionTemplate model)
    {
        public ModelDefinitionTemplate Model { get; } = model;

        public string ModelId => Model.ModelId;

        public override string ToString() => Model.ToString();
    }

    public static readonly DirectProperty<OfficialModelDefinitionForm, IReadOnlyBindableList<ItemWrapper>> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, IReadOnlyBindableList<ItemWrapper>>(
            nameof(ItemsSource),
            o => o.ItemsSource);

    public IReadOnlyBindableList<ItemWrapper> ItemsSource => _itemsSource;

    public static readonly DirectProperty<OfficialModelDefinitionForm, ModelDefinitionTemplate?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ModelDefinitionTemplate?>(
            nameof(SelectedItem),
            o => o.SelectedItem);

    public ModelDefinitionTemplate? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (ReferenceEquals(_selectedItem, value)) return;

            var oldValue = _selectedItem;
            _selectedItem = value;
            RaisePropertyChanged(SelectedItemProperty, oldValue, value);
        }
    }

    public static readonly DirectProperty<OfficialModelDefinitionForm, ItemWrapper?> SelectedItemWrapperProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ItemWrapper?>(
            nameof(SelectedItemWrapper),
            o => o.SelectedItemWrapper,
            (o, v) => o.SelectedItemWrapper = v);

    public ItemWrapper? SelectedItemWrapper
    {
        get => _selectedItemWrapper;
        set => SetSelectedItemWrapper(value);
    }

    public static readonly DirectProperty<OfficialModelDefinitionForm, string?> SelectedModelIdProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, string?>(
            nameof(SelectedModelId),
            o => o.SelectedModelId,
            (o, v) => o.SelectedModelId = v);

    public string? SelectedModelId
    {
        get => _selectedModelId;
        set => SetSelectedModelId(value);
    }

    public static readonly DirectProperty<OfficialModelDefinitionForm, bool> IsSelectedModelUnavailableProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, bool>(
            nameof(IsSelectedModelUnavailable),
            o => o.IsSelectedModelUnavailable);

    public bool IsSelectedModelUnavailable
    {
        get;
        private set => SetAndRaise(IsSelectedModelUnavailableProperty, ref field, value);
    }

    public ICloudClient CloudClient { get; } = serviceProvider.GetRequiredService<ICloudClient>();

    public IOfficialModelProvider OfficialModelProvider { get; } = serviceProvider.GetRequiredService<IOfficialModelProvider>();

    private ModelDefinitionTemplate? _selectedItem;
    private ItemWrapper? _selectedItemWrapper;
    private string? _selectedModelId = assistant.ModelId;
    private ModelDefinitionTemplate? _selectedSnapshot;
    private bool _isSynchronizingItems;

    private readonly BindableList<ItemWrapper> _itemsSource = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        OfficialModelProvider.ModelDefinitions.CollectionChanged += HandleModelDefinitionsChanged;
        ReconcileProxyList(OfficialModelProvider.ModelDefinitions);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        OfficialModelProvider.ModelDefinitions.CollectionChanged -= HandleModelDefinitionsChanged;
    }

    private void ReconcileProxyList(IReadOnlyCollection<ModelDefinitionTemplate> cloudItems)
    {
        var result = Reconcile(
            assistant,
            _selectedModelId ?? _selectedItem?.ModelId,
            _selectedSnapshot ?? _selectedItem,
            cloudItems);

        IsSelectedModelUnavailable = result.IsSelectedModelUnavailable;

        _isSynchronizingItems = true;
        try
        {
            ApplyItems(result.Items, result.TargetModelId);
        }
        finally
        {
            _isSynchronizingItems = false;
        }
    }

    private void ApplyItems(IReadOnlyList<ModelDefinitionTemplate> items, string? targetModelId)
    {
        var desiredSelectedModel = items.FirstOrDefault(x => x.ModelId == targetModelId);
        if (desiredSelectedModel is not null)
        {
            var desiredSelectedItemWrapper = _itemsSource.FirstOrDefault(x => ReferenceEquals(x.Model, desiredSelectedModel)) ??
                new ItemWrapper(desiredSelectedModel);
            if (!_itemsSource.Contains(desiredSelectedItemWrapper))
            {
                var desiredIndex = 0;
                for (; desiredIndex < items.Count; desiredIndex++)
                {
                    if (ReferenceEquals(items[desiredIndex], desiredSelectedModel)) break;
                }

                _itemsSource.Insert(Math.Min(desiredIndex, _itemsSource.Count), desiredSelectedItemWrapper);
            }

            SetSelectedItemWrapper(desiredSelectedItemWrapper, forceUpdate: true);
        }

        for (var i = _itemsSource.Count - 1; i >= 0; i--)
        {
            var item = _itemsSource[i];
            if (items.Any(model => ReferenceEquals(model, item.Model))) continue;

            _itemsSource.RemoveAt(i);
        }

        for (var desiredIndex = 0; desiredIndex < items.Count; desiredIndex++)
        {
            var model = items[desiredIndex];
            var itemWrapper = _itemsSource.FirstOrDefault(x => ReferenceEquals(x.Model, model));
            if (itemWrapper is null)
            {
                _itemsSource.Insert(desiredIndex, new ItemWrapper(model));
                continue;
            }

            var currentIndex = _itemsSource.IndexOf(itemWrapper);
            if (currentIndex != desiredIndex)
            {
                _itemsSource.Move(currentIndex, desiredIndex);
            }
        }

        SetSelectedItemWrapper(_itemsSource.FirstOrDefault(x => x.ModelId == targetModelId), forceUpdate: true);
    }

    private void SetSelectedItemWrapper(ItemWrapper? value, bool forceUpdate = false)
    {
        // ComboBox can briefly push null while ItemsSource is being rebuilt. Ignore that lifecycle artifact so the
        // selected model id remains the source of truth during list reconciliation.
        if (_isSynchronizingItems && value is null && !forceUpdate) return;
        if (value is null && !_selectedModelId.IsNullOrWhiteSpace() && !forceUpdate) return;

        if (!forceUpdate && ReferenceEquals(_selectedItemWrapper, value)) return;

        var oldItemWrapper = _selectedItemWrapper;
        _selectedItemWrapper = value;
        RaisePropertyChanged(SelectedItemWrapperProperty, oldItemWrapper, value);

        ApplySelectedModel(value?.ModelId, value?.Model, forceUpdate);
    }

    private void SetSelectedModelId(string? value, bool forceUpdate = false)
    {
        if (_isSynchronizingItems && value is null && !forceUpdate) return;

        var itemWrapper = _itemsSource.FirstOrDefault(x => x.ModelId == value);
        if (itemWrapper is not null)
        {
            SetSelectedItemWrapper(itemWrapper, forceUpdate);
            return;
        }

        var oldItemWrapper = _selectedItemWrapper;
        _selectedItemWrapper = null;
        RaisePropertyChanged(SelectedItemWrapperProperty, oldItemWrapper, null);
        ApplySelectedModel(value, null, forceUpdate);
    }

    private void ApplySelectedModel(string? modelId, ModelDefinitionTemplate? model, bool forceUpdate)
    {
        if (!forceUpdate &&
            string.Equals(_selectedModelId, modelId, StringComparison.Ordinal) &&
            ReferenceEquals(_selectedItem, model))
        {
            return;
        }

        var oldValue = _selectedModelId;
        _selectedModelId = modelId;
        RaisePropertyChanged(SelectedModelIdProperty, oldValue, modelId);

        SelectedItem = model;
        if (model is not null)
        {
            _selectedSnapshot = model;
            assistant.ApplyTemplate(model);
        }

        UpdateSelectedModelUnavailable(OfficialModelProvider.ModelDefinitions);
    }

    private void HandleModelDefinitionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.PostOnDemand(() => ReconcileProxyList(OfficialModelProvider.ModelDefinitions));
    }

    private void UpdateSelectedModelUnavailable(IReadOnlyCollection<ModelDefinitionTemplate> cloudItems)
    {
        IsSelectedModelUnavailable = _selectedModelId is { Length: > 0 } modelId &&
            cloudItems.Count > 0 &&
            cloudItems.All(x => x.ModelId != modelId);
    }

    [RelayCommand]
    private Task RefreshAsync() => OfficialModelProvider.RefreshAsync(this);

    void IExceptionHandler.HandleException(Exception exception, string? message, object? source, int lineNumber)
    {
        ToastManager.Error(message ?? LocaleResolver.Common_Error, exception.GetFriendlyMessage());
    }

    internal sealed record ReconcileResult(
        IReadOnlyList<ModelDefinitionTemplate> Items,
        string? TargetModelId,
        ModelDefinitionTemplate? SelectedItem,
        bool IsSelectedModelUnavailable);

    /// <summary>
    /// Rebuilds the UI model list from the cloud list while preserving the selected model id.
    /// </summary>
    internal static ReconcileResult Reconcile(
        Assistant assistant,
        string? targetModelId,
        ModelDefinitionTemplate? selectedSnapshot,
        IReadOnlyCollection<ModelDefinitionTemplate> cloudItems)
    {
        var finalItems = cloudItems.ToList();
        var desiredModelId = FirstNonEmpty(targetModelId, selectedSnapshot?.ModelId, assistant.ModelId);
        if (desiredModelId.IsNullOrWhiteSpace())
        {
            return new ReconcileResult(finalItems, null, null, false);
        }

        var selectedItem = finalItems.FirstOrDefault(x => x.ModelId == desiredModelId);
        var isUnavailable = cloudItems.Count > 0 && selectedItem is null;
        if (selectedItem is null)
        {
            selectedItem = CreateFallbackSnapshot(assistant, desiredModelId, selectedSnapshot);
            finalItems.Insert(0, selectedItem);
        }

        return new ReconcileResult(finalItems, desiredModelId, selectedItem, isUnavailable);
    }

    private static ModelDefinitionTemplate CreateFallbackSnapshot(
        Assistant assistant,
        string modelId,
        ModelDefinitionTemplate? selectedSnapshot)
    {
        if (selectedSnapshot is not null && selectedSnapshot.ModelId == modelId)
        {
            return selectedSnapshot;
        }

        return new ModelDefinitionTemplate
        {
            ModelId = modelId,
            Name = modelId,
            SupportsToolCall = assistant.SupportsToolCall,
            SupportsTemperature = assistant.SupportsTemperature,
            InputModalities = assistant.InputModalities,
            OutputModalities = assistant.OutputModalities,
            ContextLimit = assistant.ContextLimit,
            OutputLimit = assistant.OutputLimit,
            Specializations = assistant.Specializations,
            DeprecationDate = assistant.DeprecationDate
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !value.IsNullOrWhiteSpace());
}
