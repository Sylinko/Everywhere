using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Collections;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class PresetModelDefinitionSelector(IPresetModelProvider provider, PresetBasedAssistantConfigurator configurator)
    : TemplatedControl, IExceptionHandler
{
    public IPresetModelProvider Provider { get; } = provider;
    public static readonly DirectProperty<PresetModelDefinitionSelector, IReadOnlyBindableList<ModelDefinitionTemplate>> ItemsProperty =
        AvaloniaProperty.RegisterDirect<PresetModelDefinitionSelector, IReadOnlyBindableList<ModelDefinitionTemplate>>(nameof(Items), o => o.Items);
    public IReadOnlyBindableList<ModelDefinitionTemplate> Items => _items;

    public static readonly DirectProperty<PresetModelDefinitionSelector, ModelDefinitionTemplate?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<PresetModelDefinitionSelector, ModelDefinitionTemplate?>(nameof(SelectedItem),
            o => o.SelectedItem, (o, v) => o.SelectedItem = v);

    public ModelDefinitionTemplate? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // ComboBox can transiently clear selection while items are reconciled.
            if (_reconciling || value is null) return;
            if (SetAndRaise(SelectedItemProperty, ref _selectedItem, value)) configurator.ModelDefinitionTemplate = value;
        }
    }

    public static readonly DirectProperty<PresetModelDefinitionSelector, bool> IsUnlistedProperty =
        AvaloniaProperty.RegisterDirect<PresetModelDefinitionSelector, bool>(nameof(IsUnlisted), o => o.IsUnlisted);

    public bool IsUnlisted
    {
        get => _isUnlisted;
        private set => SetAndRaise(IsUnlistedProperty, ref _isUnlisted, value);
    }

    private readonly BindableList<ModelDefinitionTemplate> _items = [];
    private ModelDefinitionTemplate? _selectedItem;
    private bool _isUnlisted;
    private bool _attached;
    private bool _reconciling;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Provider.ModelsChanged += HandleModelsChanged;
        configurator.PropertyChanged += HandleConfigurationChanged;
        Reconcile();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Provider.ModelsChanged -= HandleModelsChanged;
        configurator.PropertyChanged -= HandleConfigurationChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleModelsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.PostOnDemand(Reconcile);
    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.PostOnDemand(Reconcile);

    private void Reconcile()
    {
        if (!_attached || _reconciling) return;
        _reconciling = true;
        try
        {
            var selected = configurator.ModelDefinitionTemplate;
            var desired = Provider.GetModelDefinitions(configurator.ModelProviderTemplateId).ToList();
            IsUnlisted = selected is not null && desired.All(m => m.ModelId != selected.ModelId);
            if (IsUnlisted && selected is not null) desired.Insert(0, selected);
            for (var i = 0; i < desired.Count; i++)
            {
                var model = desired[i];
                var existing = _items.FirstOrDefault(m => m.ModelId == model.ModelId);
                if (existing is null) _items.Insert(i, model);
                else
                {
                    var index = _items.IndexOf(existing);
                    if (index != i) _items.Move(index, i);
                    // Preserve the displayed object for unchanged models, including fallback snapshots.
                    if (!HasSameMetadata(existing, model)) _items[i] = model;
                }
            }
            while (_items.Count > desired.Count) _items.RemoveAt(_items.Count - 1);
            SetAndRaise(SelectedItemProperty, ref _selectedItem, _items.FirstOrDefault(m => m.ModelId == selected?.ModelId));
        }
        finally { _reconciling = false; }
    }

    [RelayCommand]
    private Task RefreshAsync() => Provider.RefreshAsync(this);

    private static bool HasSameMetadata(ModelDefinitionTemplate left, ModelDefinitionTemplate right) =>
        left.ModelId == right.ModelId && left.Name == right.Name &&
        left.SupportsToolCall == right.SupportsToolCall && left.InputModalities == right.InputModalities &&
        left.OutputModalities == right.OutputModalities && left.ContextLimit == right.ContextLimit &&
        left.OutputLimit == right.OutputLimit && left.Specializations == right.Specializations &&
        left.DeprecationDate == right.DeprecationDate && left.ReleaseDate == right.ReleaseDate &&
        left.KnowledgeCutoff == right.KnowledgeCutoff;

    void IExceptionHandler.HandleException(Exception exception, string? message, object? source, int lineNumber)
    {
        if (_attached) ToastManager.Error(message ?? LocaleResolver.Common_Error, exception.GetFriendlyMessage());
    }
}
