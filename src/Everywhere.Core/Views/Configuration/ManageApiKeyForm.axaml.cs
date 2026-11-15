using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using ShadUI;

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
        /// Reads the stored key for editing or copying and saves edits on focus loss.
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
                OnPropertyChanged(nameof(RevealedSecretKey));
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RevealedSecretKey))]
        public partial bool IsApiKeyVisible { get; set; }

        /// <summary>
        /// Hidden controls still evaluate their bindings. Only open the vault for a revealed row.
        /// </summary>
        public string? RevealedSecretKey => IsApiKeyVisible ? SecretKey : null;

        [RelayCommand]
        private async Task CopyApiKeyToClipboardAsync()
        {
            if (SecretKey is not { Length: > 0 } secretKey) return;

            await App.Clipboard.SetTextAsync(secretKey);
            ToastManager.Success(LocaleKey.Common_Copied.I18N());
        }
    }

    public IReadOnlyBindableList<DataGridApiKeyModel> ItemsSource { get; }

    private readonly Settings _settings;
    private readonly string? _defaultName;
    private readonly ObservableCollection<ApiKey> _apiKeys;
    private readonly IDisposable _itemsSourceSubscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManageApiKeyForm"/> class.
    /// </summary>
    /// <param name="itemsSource"></param>
    /// <param name="defaultName"></param>
    /// <param name="settings">Settings that own the key lists and their references.</param>
    public ManageApiKeyForm(ObservableCollection<ApiKey> itemsSource, string? defaultName, Settings settings)
    {
        _defaultName = defaultName;
        _settings = settings;
        _apiKeys = itemsSource;

        ItemsSource = _apiKeys.ToObservableChangeSet()
            .Transform(static apiKey => new DataGridApiKeyModel(apiKey))
            .BindEx(out _itemsSourceSubscription);
    }

    [RelayCommand]
    private async Task AddApiKeyAsync(CancellationToken cancellationToken)
    {
        var form = new CreateApiKeyForm(_defaultName);
        var result = await DialogManager
            .CreateDialog(form, LocaleKey.ApiKeyComboBox_AddApiKey.I18N(), TopLevel.GetTopLevel(this))
            .WithPrimaryButton(
                LocaleKey.Common_OK.I18N(),
                (_, e) => e.Cancel = !form.ApiKey.ValidateAndSave())
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync(cancellationToken);
        if (result != DialogResult.Primary) return;

        _apiKeys.Add(form.ApiKey);
    }

    [RelayCommand]
    private async Task DeleteApiKeyAsync(DataGridApiKeyModel model)
    {
        IsEnabled = false;
        try
        {
            await _settings.RemoveApiKeyAsync(_apiKeys, model.ApiKey);
        }
        catch (Exception exception)
        {
            ToastManager.Error(LocaleKey.Common_Error.I18N(), HandledSystemException.Handle(exception).GetFriendlyMessage());
        }
        finally
        {
            IsEnabled = true;
        }
    }

    public void Dispose() => _itemsSourceSubscription.Dispose();
}
