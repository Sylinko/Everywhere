using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using ShadUI;

namespace Everywhere.Views;

[TemplatePart(Name = "PART_ComboBox", Type = typeof(ComboBox), IsRequired = true)]
public sealed partial class ApiKeyComboBox : TemplatedControl
{
    /// <summary>
    /// Defines the <see cref="SelectedId"/> property.
    /// </summary>
    public static readonly StyledProperty<Guid> SelectedIdProperty =
        AvaloniaProperty.Register<ApiKeyComboBox, Guid>(nameof(SelectedId), enableDataValidation: true);

    /// <summary>
    /// Gets or sets the API key ID.
    /// </summary>
    public Guid SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="DefaultName"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> DefaultNameProperty =
        AvaloniaProperty.Register<ApiKeyComboBox, string?>(nameof(DefaultName));

    /// <summary>
    /// Gets or sets the default name for a new API key.
    /// </summary>
    public string? DefaultName
    {
        get => GetValue(DefaultNameProperty);
        set => SetValue(DefaultNameProperty, value);
    }

    public IReadOnlyBindableList<ApiKey> ItemsSource { get; }

    private readonly ObservableCollection<ApiKey> _itemsSource;
    private readonly BindableList<ApiKey> _items = [];

    private ComboBox? _comboBox;

    public ApiKeyComboBox(ObservableCollection<ApiKey> itemsSource)
    {
        _itemsSource = itemsSource;

        ItemsSource = _items;
        RebuildItemsSource();
    }

    [RelayCommand]
    private async Task AddApiKeyAsync(CancellationToken cancellationToken)
    {
        var form = new CreateApiKeyForm(DefaultName);
        var result = await DialogManager
            .CreateDialog(form, LocaleResolver.ApiKeyComboBox_AddApiKey)
            .WithPrimaryButton(
                LocaleResolver.Common_OK,
                (_, e) => e.Cancel = !form.ApiKey.ValidateAndSave())
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;

        var apiKey = form.ApiKey;
        _itemsSource.Add(apiKey);
        SelectedId = apiKey.Id;
    }

    [RelayCommand]
    private async Task ManageApiKeyAsync(CancellationToken cancellationToken)
    {
        using var form = new ManageApiKeyForm(_itemsSource, DefaultName);
        await DialogManager
            .CreateDialog(form, LocaleResolver.ApiKeyComboBox_ManageApiKey)
            .WithPrimaryButton(LocaleResolver.Common_OK)
            .ShowAsync(cancellationToken);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _comboBox = e.NameScope.Find<ComboBox>("PART_ComboBox");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _itemsSource.CollectionChanged += HandleSourceCollectionChanged;
        RebuildItemsSource();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _itemsSource.CollectionChanged -= HandleSourceCollectionChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void UpdateDataValidation(
        AvaloniaProperty property,
        BindingValueType state,
        Exception? error)
    {
        if (property == SelectedIdProperty && _comboBox is not null)
        {
            DataValidationErrors.SetError(_comboBox, error);
        }
    }

    private void HandleSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildItemsSource();
    }

    private void RebuildItemsSource()
    {
        _items.Clear();
        _items.Add(ApiKey.Empty);
        foreach (var apiKey in _itemsSource)
        {
            _items.Add(apiKey);
        }
    }
}
