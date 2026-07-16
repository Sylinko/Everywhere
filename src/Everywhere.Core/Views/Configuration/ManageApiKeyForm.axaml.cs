using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Collections;
using Everywhere.Configuration;
using ShadUI;
using ZLinq;

namespace Everywhere.Views;

public sealed partial class ManageApiKeyForm : TemplatedControl, IDisposable
{
    public sealed partial class DataGridApiKeyModel(ApiKey apiKey) : ObservableObject
    {
        public ApiKey ApiKey { get; } = apiKey;

        /// <summary>
        /// Gets or sets the name of the API key.
        /// Should use LostFocus and if value is null or whitespace, do not update the underlying model.
        /// </summary>
        public string? Name
        {
            get => ApiKey.Name;
            set
            {
                if (ApiKey.Name == value) return;
                if (value.IsNullOrWhiteSpace())
                {
                    OnPropertyChanged();
                    return;
                }

                ApiKey.Name = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the name of the API key.
        /// This property is bound to the data grid and updates the underlying ApiKey model.
        /// Should use LostFocus or similar to avoid excessive validation calls.
        /// </summary>
        public string? SecretKey
        {
            get => ApiKey.SecretKey;
            set
            {
                if (ApiKey.SecretKey == value) return;
                if (value.IsNullOrWhiteSpace())
                {
                    OnPropertyChanged();
                    return;
                }

                ApiKey.SecretKey = value;
                ApiKey.ValidateAndSave();
                OnPropertyChanged();
            }
        }

        [ObservableProperty]
        public partial bool IsChecked { get; set; }

        [ObservableProperty]
        public partial bool IsApiKeyVisible { get; set; }

        [RelayCommand]
        private async Task CopyApiKeyToClipboardAsync()
        {
            if (SecretKey is not { Length: > 0 } secretKey) return;

            await App.Clipboard.SetTextAsync(secretKey);
            ToastManager.Success(LocaleResolver.Common_Copied);
        }
    }

    public IReadOnlyBindableList<DataGridApiKeyModel> ItemsSource { get; }

    public static readonly DirectProperty<ManageApiKeyForm, bool?> IsApiKeysCheckedProperty =
        AvaloniaProperty.RegisterDirect<ManageApiKeyForm, bool?>(
        nameof(IsApiKeysChecked),
        o => o.IsApiKeysChecked,
        (o, v) => o.IsApiKeysChecked = v);

    public bool? IsApiKeysChecked
    {
        get
        {
            var checkedCount = ItemsSource.AsValueEnumerable().Count(item => item.IsChecked);
            if (checkedCount == 0) return false;
            if (checkedCount == ItemsSource.Count) return true;
            return null;
        }
        set
        {
            switch (value)
            {
                case true:
                {
                    foreach (var item in ItemsSource.AsValueEnumerable())
                    {
                        item.IsChecked = true;
                    }
                    break;
                }
                case false:
                {
                    foreach (var item in ItemsSource.AsValueEnumerable())
                    {
                        item.IsChecked = false;
                    }
                    break;
                }
            }

            RaisePropertyChanged(IsApiKeysCheckedProperty, null, value);
            DeleteApiKeysCommand.NotifyCanExecuteChanged();
        }
    }

    public static readonly DirectProperty<ManageApiKeyForm, bool> CanDeleteApiKeysProperty =
        AvaloniaProperty.RegisterDirect<ManageApiKeyForm, bool>(
        nameof(CanDeleteApiKeys),
        o => o.CanDeleteApiKeys);

    public bool CanDeleteApiKeys => ItemsSource.AsValueEnumerable().Any(item => item.IsChecked);

    private readonly string? _defaultName;
    private readonly ObservableCollection<ApiKey> _apiKeys;
    private readonly IDisposable _itemsSourceSubscription;
    private readonly IDisposable _selectionSubscription;
    private readonly IObservableList<DataGridApiKeyModel> _sharedList;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManageApiKeyForm"/> class.
    /// </summary>
    /// <param name="itemsSource"></param>
    /// <param name="defaultName"></param>
    public ManageApiKeyForm(ObservableCollection<ApiKey> itemsSource, string? defaultName)
    {
        _defaultName = defaultName;
        _apiKeys = itemsSource;

        _sharedList = _apiKeys
            .ToObservableChangeSet()
            .Transform(apiKey => new DataGridApiKeyModel(apiKey))
            .AsObservableList();

        ItemsSource = _sharedList.Connect().BindEx(out _itemsSourceSubscription);

        _selectionSubscription = _sharedList.Connect()
            .AutoRefresh(x => x.IsChecked)
            .ToCollection()
            .Subscribe(_ =>
            {
                RaisePropertyChanged(IsApiKeysCheckedProperty, null, IsApiKeysChecked);
                RaisePropertyChanged(CanDeleteApiKeysProperty, false, CanDeleteApiKeys);
                DeleteApiKeysCommand.NotifyCanExecuteChanged();
            });
    }

    [RelayCommand]
    private async Task AddApiKeyAsync(CancellationToken cancellationToken)
    {
        var form = new CreateApiKeyForm(_defaultName);
        var result = await DialogManager
            .CreateDialog(form, LocaleResolver.ApiKeyComboBox_AddApiKey, TopLevel.GetTopLevel(this))
            .WithPrimaryButton(
                LocaleResolver.Common_OK,
                (_, e) => e.Cancel = !form.ApiKey.ValidateAndSave())
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;

        _apiKeys.Add(form.ApiKey);
    }

    [RelayCommand]
    private void DeleteApiKey(DataGridApiKeyModel model)
    {
        _apiKeys.Remove(model.ApiKey);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteApiKeys))]
    private async Task DeleteApiKeysAsync()
    {
        var keysToDelete = ItemsSource
            .AsValueEnumerable()
            .Where(item => item.IsChecked)
            .Select(item => item.ApiKey)
            .ToList();

        if (keysToDelete.Count == 0) return;

        var result = await DialogManager.CreateDialog(
                LocaleResolver.ManageApiKeyForm_DeleteApiKeys_Dialog_Message.Format(keysToDelete.Count),
                LocaleResolver.Common_Warning,
                TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleResolver.Common_Yes)
            .WithCancelButton(LocaleResolver.Common_No)
            .ShowAsync();

        if (result != DialogResult.Primary) return;

        foreach (var key in keysToDelete.AsValueEnumerable())
        {
            _apiKeys.Remove(key);
        }
    }

    public void Dispose()
    {
        _itemsSourceSubscription.Dispose();
        _selectionSubscription.Dispose();
        _sharedList.Dispose();
    }
}
