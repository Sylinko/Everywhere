using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Messages;
using Everywhere.TextSelection;
using Everywhere.Views;

namespace Everywhere.ViewModels;

/// <summary>
/// Full first-use onboarding flow. Each instance owns one assistant draft and one guided prompt
/// draft while preserving intermediate choices across source, mode, and back navigation.
/// Saved artifacts survive cancellation, whereas completion version only advances upon shortcut confirmation.
/// </summary>
public sealed partial class WelcomeFlow : OobeFlow, IExceptionHandler
{
    public Settings Settings { get; }
    public ICloudClient CloudClient { get; }
    public CustomAssistant Assistant { get; }
    public PromptEditorViewModel Personalization { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloudSource))]
    [NotifyPropertyChangedFor(nameof(IsOwnSource))]
    [NotifyPropertyChangedFor(nameof(IsPresetMode))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedOwnMode))]
    [NotifyPropertyChangedFor(nameof(DisplayProviderTemplate))]
    [NotifyPropertyChangedFor(nameof(DisplayProviderHost))]
    [NotifyPropertyChangedFor(nameof(HasTelemetryHeader))]
    [NotifyPropertyChangedFor(nameof(ShowTelemetryMetrics))]
    [NotifyPropertyChangedFor(nameof(CanSelectCatalogModel))]
    [NotifyPropertyChangedFor(nameof(IsCatalogRefreshing))]
    [NotifyPropertyChangedFor(nameof(CanRefreshCatalog))]
    [NotifyPropertyChangedFor(nameof(EndpointPreviewText))]
    public partial WelcomeModelSource? SelectedSource { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresetMode))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedOwnMode))]
    [NotifyPropertyChangedFor(nameof(DisplayProviderTemplate))]
    [NotifyPropertyChangedFor(nameof(HasTelemetryHeader))]
    [NotifyPropertyChangedFor(nameof(ShowTelemetryMetrics))]
    [NotifyPropertyChangedFor(nameof(CanSelectCatalogModel))]
    [NotifyPropertyChangedFor(nameof(IsCatalogRefreshing))]
    [NotifyPropertyChangedFor(nameof(CanRefreshCatalog))]
    [NotifyPropertyChangedFor(nameof(EndpointPreviewText))]
    public partial bool IsAdvancedMode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTelemetryHeader))]
    public partial bool IsConnectivityChecked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTelemetryHeader))]
    public partial bool IsTestingConnectivity { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<AssistantCatalogItem> CatalogModels { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsSelectedCatalogModelUnlisted { get; private set; }

    public IDynamicLocaleKey PreviewQuestionKey => Personalization.PreviewQuestionKey;

    public IDynamicLocaleKey PreviewReplyKey => Personalization.PreviewReplyKey;

    public bool IsCloudSource => SelectedSource == WelcomeModelSource.Cloud;
    public bool IsOwnSource => SelectedSource == WelcomeModelSource.Own;
    public bool IsPresetMode => !IsCloudSource && !IsAdvancedMode;
    public bool IsAdvancedOwnMode => !IsCloudSource && IsAdvancedMode;

    public PresetAssistantConfiguration? PresetConfiguration => Assistant.Configuration as PresetAssistantConfiguration;
    public AdvancedAssistantConfiguration? AdvancedConfiguration => Assistant.Configuration as AdvancedAssistantConfiguration;
    public OfficialAssistantConfiguration? OfficialConfiguration => Assistant.Configuration as OfficialAssistantConfiguration;

    public static IReadOnlyList<ModelProviderTemplate> PresetProviders => PresetModelTemplates.Providers;

    /// <summary>
    /// Gets or sets the built-in provider selected in preset onboarding mode.
    /// </summary>
    public ModelProviderTemplate? SelectedPresetProvider
    {
        get => PresetConfiguration?.ModelProviderTemplate;
        set
        {
            if (value is null || PresetConfiguration is not { } preset || value.Id == preset.ProviderId) return;

            if (_assistantCatalog is not null)
            {
                _assistantCatalog.ApplyPresetProvider(preset, value);
            }
            else
            {
                lock (preset)
                {
                    preset.ApiKey = Guid.Empty;
                    preset.ProviderId = value.Id;
                    preset.Endpoint = value.Endpoint;
                    preset.Schema = value.Schema;
                    preset.Apply((ModelDefinitionTemplate?)null);
                }
            }

            ObserveSelectedApiKey();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedApiKey));
            OnPropertyChanged(nameof(DisplayProviderTemplate));
            OnPropertyChanged(nameof(HasTelemetryHeader));
            OnPropertyChanged(nameof(CanSelectCatalogModel));
            OnPropertyChanged(nameof(CanRefreshCatalog));
            ReconcileCatalogSelection();
            NotifyModelCapabilitiesChanged();
            RefreshCatalogCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Gets or sets the selected preset or official catalog model entry for the active route.
    /// </summary>
    public AssistantCatalogItem? SelectedCatalogModel
    {
        get => _selectedCatalogModel;
        set => SetSelectedCatalogModel(value, apply: true);
    }

    public bool CanSelectCatalogModel =>
        IsNotBusy && (IsCloudSource || (IsPresetMode && DisplayProviderTemplate is not null));

    public bool IsCatalogRefreshing =>
        IsCloudSource ? _officialModelProvider?.IsRefreshing == true : _presetModelProvider?.IsRefreshing == true;

    public bool CanRefreshCatalog =>
        IsNotBusy &&
        !IsCatalogRefreshing &&
        (IsCloudSource ? _officialModelProvider is not null : _presetModelProvider is not null && DisplayProviderTemplate is not null);

    public bool ShowTelemetryMetrics =>
        !IsAdvancedOwnMode && HasConfiguredModel && HasTokenLimits && (IsCloudSource || DisplayProviderTemplate is not null);

    public IReadOnlyList<ApiKey> AvailableApiKeys => _availableApiKeys;

    /// <summary>
    /// Gets or sets the API key selected for the active preset or advanced configuration.
    /// </summary>
    public ApiKey? SelectedApiKey
    {
        get => _availableApiKeys.AsValueEnumerable().FirstOrDefault(key => key.Id == Assistant.Configuration.ApiKey) ?? ApiKey.Empty;
        set
        {
            if (value is null || Assistant.Configuration.ApiKey == value.Id) return;
            Assistant.Configuration.ApiKey = value.Id;
        }
    }

    /// <summary>
    /// Gets the live normalized request URL preview for advanced mode, or <see langword="null"/> when empty.
    /// </summary>
    public string? EndpointPreviewText =>
        IsAdvancedOwnMode &&
        Assistant.Configuration is AdvancedAssistantConfiguration advanced &&
        !string.IsNullOrWhiteSpace(advanced.Endpoint) ?
            advanced.Schema.PreviewEndpoint(advanced.Endpoint) :
            null;

    /// <summary>
    /// Gets the protocol-specific example endpoint URL displayed as placeholder in custom protocol mode.
    /// </summary>
    public string AdvancedEndpointPlaceholder =>
        (Assistant.Configuration as AdvancedAssistantConfiguration)?.Schema.GetDefaultEndpoint()
        ?? ModelProviderSchema.OpenAI.GetDefaultEndpoint();

    /// <summary>
    /// Gets or sets the context window limit text, returning <see langword="null"/> at zero so placeholders render cleanly.
    /// </summary>
    public string? AdvancedContextLimitText
    {
        get => Assistant.Configuration.ContextLimit > 0 ? Assistant.Configuration.ContextLimit.ToString(CultureInfo.InvariantCulture) : null;
        set => ApplyTokenLimit(
            value,
            Assistant.Configuration.ContextLimit,
            static (config, limit) => config.ContextLimit = limit,
            nameof(AdvancedContextLimitText),
            nameof(ContextLimitText));
    }

    /// <summary>
    /// Gets or sets the max output token limit text, returning <see langword="null"/> at zero so placeholders render cleanly.
    /// </summary>
    public string? AdvancedOutputLimitText
    {
        get => Assistant.Configuration.OutputLimit > 0 ? Assistant.Configuration.OutputLimit.ToString(CultureInfo.InvariantCulture) : null;
        set => ApplyTokenLimit(
            value,
            Assistant.Configuration.OutputLimit,
            static (config, limit) => config.OutputLimit = limit,
            nameof(AdvancedOutputLimitText),
            nameof(OutputLimitText));
    }

    /// <summary>
    /// Gets the active preset provider template when configuring a built-in provider, or <see langword="null"/>.
    /// </summary>
    public ModelProviderTemplate? DisplayProviderTemplate =>
        IsPresetMode && Assistant.Configuration is PresetAssistantConfiguration preset ? preset.ModelProviderTemplate : null;

    /// <summary>
    /// Gets whether the telemetry stage card has any top header badge, provider identity, or active status to display.
    /// </summary>
    public bool HasTelemetryHeader =>
        IsCloudSource ||
        (IsAdvancedOwnMode && !string.IsNullOrEmpty(DisplayProviderHost)) ||
        DisplayProviderTemplate is not null ||
        IsTestingConnectivity ||
        IsConnectivityChecked;

    /// <summary>
    /// Gets the signature preset providers surfaced as stacked avatars on the model-source card.
    /// </summary>
    public static IReadOnlyList<ModelProviderTemplate> FeaturedOwnProviders { get; } =
        ((ReadOnlySpan<string>)["openai", "google", "anthropic", "deepseek", "moonshotai", "minimax"])
        .AsValueEnumerable()
        .Select(static id => PresetModelTemplates.Providers.FirstOrDefault(p => p.Id == id))
        .OfType<ModelProviderTemplate>()
        .ToArray();

    /// <summary>
    /// Gets the count of additional preset providers beyond <see cref="FeaturedOwnProviders"/>.
    /// </summary>
    public static int RemainingOwnProviderCount => Math.Max(0, PresetModelTemplates.Providers.Length - FeaturedOwnProviders.Count);

    public bool IsSignedIn => CloudClient.UserProfile is not null;
    public bool CanLogIn => IsNotBusy && !IsSignedIn;
    public bool HasScenarios => Personalization.ScenarioOptions.AsValueEnumerable().Any(static option => option.IsSelected);
    public bool ShowScenarioRequiredHint => _hasInteractedWithScenarios && !HasScenarios;
    public bool HasValidShortcut => Settings.Shortcut.ChatWindow.Main.IsValid;
    public bool IsFontSizeSmall => Settings.Display.FontSize == -1;
    public bool IsFontSizeMedium => Settings.Display.FontSize == 0;
    public bool IsFontSizeLarge => Settings.Display.FontSize == 2;

    public string? DisplayModelName =>
        !string.IsNullOrWhiteSpace(Assistant.Configuration.Name) ? Assistant.Configuration.Name :
        !string.IsNullOrWhiteSpace(Assistant.Configuration.ModelId) ? Assistant.Configuration.ModelId :
        null;

    public bool HasConfiguredModel => !string.IsNullOrWhiteSpace(DisplayModelName);

    public string DisplayProviderHost
    {
        get
        {
            if (IsCloudSource) return "Everywhere Cloud";
            if (Uri.TryCreate(Assistant.Configuration.Endpoint, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            return string.Empty;
        }
    }

    public string? ContextLimitText => FormatTokenCount(Assistant.Configuration.ContextLimit);
    public string? OutputLimitText => FormatTokenCount(Assistant.Configuration.OutputLimit);
    public bool HasTokenLimits => ContextLimitText is not null || OutputLimitText is not null;

    public bool SupportsTextInput
    {
        get => Assistant.Configuration.InputModalities.HasFlag(Modalities.Text);
        set => SetInputModality(Modalities.Text, value);
    }

    public bool SupportsImageInput
    {
        get => Assistant.Configuration.InputModalities.HasFlag(Modalities.Image);
        set => SetInputModality(Modalities.Image, value);
    }

    public bool SupportsAudioInput
    {
        get => Assistant.Configuration.InputModalities.HasFlag(Modalities.Audio);
        set => SetInputModality(Modalities.Audio, value);
    }

    public bool SupportsVideoInput
    {
        get => Assistant.Configuration.InputModalities.HasFlag(Modalities.Video);
        set => SetInputModality(Modalities.Video, value);
    }

    public bool SupportsPdfInput
    {
        get => Assistant.Configuration.InputModalities.HasFlag(Modalities.Pdf);
        set => SetInputModality(Modalities.Pdf, value);
    }

    public bool SupportsTools
    {
        get => Assistant.Configuration.SupportsToolCall;
        set
        {
            if (Assistant.Configuration.SupportsToolCall == value) return;
            Assistant.Configuration.SupportsToolCall = value;
        }
    }

    public bool SupportsReasoning => Assistant.Configuration.DefaultReasoningEffortValues is { Count: > 0 };

    public TextSelectionToolbarActionsManager? TextSelectionActionsManager { get; }

    private readonly IModelSessionFactory _modelSessionFactory;
    private readonly AssistantCatalog? _assistantCatalog;
    private readonly IPresetModelProvider? _presetModelProvider;
    private readonly IOfficialModelProvider? _officialModelProvider;
    private readonly TextSelectionController? _textSelectionController;
    private readonly BindableList<ApiKey> _availableApiKeys = [];
    private readonly Dictionary<Type, AssistantConfiguration> _configurationDrafts = [];

    private AssistantConfiguration _observedConfiguration;
    private AssistantCatalogItem? _selectedCatalogModel;
    private ObservableCollection<ApiKey> _observedApiKeys;
    private ApiKey? _observedApiKey;
    private int _configurationRevision;
    private bool _isSynchronizingCatalog;
    private bool _isApplyingCatalogSelection;
    private bool _hasInteractedWithScenarios;
    private bool _isDisposed;

    public WelcomeFlow(
        Settings settings,
        ICloudClient cloudClient,
        IModelSessionFactory modelSessionFactory,
        PromptEditorViewModel personalization,
        AssistantCatalog? assistantCatalog = null,
        IPresetModelProvider? presetModelProvider = null,
        IOfficialModelProvider? officialModelProvider = null,
        TextSelectionController? textSelectionController = null,
        TextSelectionToolbarActionsManager? textSelectionToolbarActionsManager = null
    )
    {
        Settings = settings;
        CloudClient = cloudClient;
        Personalization = personalization;
        _modelSessionFactory = modelSessionFactory;
        _assistantCatalog = assistantCatalog;
        _presetModelProvider = presetModelProvider;
        _officialModelProvider = officialModelProvider;
        _textSelectionController = textSelectionController;
        TextSelectionActionsManager = textSelectionToolbarActionsManager;

        Assistant = new CustomAssistant { Name = LocaleKey.CustomAssistant_Name_Default.I18N() };
        _observedConfiguration = Assistant.Configuration;
        _configurationDrafts.Add(_observedConfiguration.GetType(), _observedConfiguration);
        _observedConfiguration.PropertyChanged += HandleConfigurationChanged;
        _observedApiKeys = Settings.Model.ApiKeys;
        _observedApiKeys.CollectionChanged += HandleApiKeysChanged;
        RebuildAvailableApiKeys();

        Settings.Model.PropertyChanged += HandleModelSettingsChanged;
        Assistant.PropertyChanged += HandleAssistantChanged;
        CloudClient.PropertyChanged += HandleCloudClientChanged;
        Settings.Shortcut.ChatWindow.PropertyChanged += HandleShortcutChanged;
        Settings.Display.PropertyChanged += HandleDisplaySettingsChanged;
        if (_presetModelProvider is not null)
        {
            _presetModelProvider.CatalogChanged += HandleCatalogChanged;
            _presetModelProvider.PropertyChanged += HandleProviderPropertyChanged;
        }
        if (_officialModelProvider is not null)
        {
            _officialModelProvider.CatalogChanged += HandleCatalogChanged;
            _officialModelProvider.PropertyChanged += HandleProviderPropertyChanged;
        }

        Personalization.OpenForCreate();
        // Unlike Prompt Manager, onboarding requires an explicit scenario choice.
        foreach (var option in Personalization.ScenarioOptions.Where(static option => option.IsSelected))
        {
            Personalization.ToggleScenarioCommand.Execute(option);
        }

        foreach (var option in Personalization.ScenarioOptions)
        {
            option.PropertyChanged += HandleScenarioOptionPropertyChanged;
        }

        Personalization.PropertyChanged += HandlePersonalizationChanged;

        InitializeSteps(
        [
            new WelcomeIntroStep(this),
            new WelcomeSourceStep(this),
            new WelcomeLoginStep(this),
            new WelcomeConfigurationStep(this),
            new WelcomePersonalizationStep(this),
            new WelcomeShortcutStep(this),
            new WelcomeTextSelectionStep(this),
            new WelcomeFinishStep(this)
        ]);
    }

    /// <summary>
    /// Tests the current configuration and saves/selects this run's assistant idempotently.
    /// Catalog or setting edits during the request invalidate its result.
    /// </summary>
    public async Task<bool> CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        if (IsConnectivityChecked) return true;
        if (!Assistant.Configuration.Validate())
        {
            ErrorKey = new DynamicLocaleKey(LocaleKey.Oobe_InvalidConfiguration);
            return false;
        }

        if (IsCloudSource && !IsSignedIn)
        {
            ErrorKey = new DynamicLocaleKey(LocaleKey.Oobe_LoginRequired);
            return false;
        }

        var revision = _configurationRevision;
        ModelSession? session = null;
        IsTestingConnectivity = true;
        try
        {
            session = _modelSessionFactory.Create(Assistant);
            await session.CheckConnectivityAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _configurationRevision)
            {
                ErrorKey = new DynamicLocaleKey(LocaleKey.Oobe_ConfigurationChanged);
                return false;
            }

            if (!Settings.Model.CustomAssistants.Contains(Assistant))
            {
                Settings.Model.CustomAssistants.Add(Assistant);
            }

            Settings.Model.SelectedCustomAssistant = Assistant;
            IsConnectivityChecked = true;
            RefreshNavigation();
            RequestCelebration();
            // Remain on configuration step so the user sees verified badge + celebration before advancing.
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ChatExceptionNormalizer.Handle(exception, session);
        }
        finally
        {
            IsTestingConnectivity = false;
            session?.Dispose();
        }
    }

    /// <summary>
    /// Saves the guided prompt via Prompt Manager and binds its identity to this run's assistant.
    /// </summary>
    public async Task<bool> SavePersonalizationAsync(CancellationToken cancellationToken)
    {
        if (!IsConnectivityChecked || !HasScenarios) return false;
        var prompt = await Personalization.SavePromptAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (prompt is null)
        {
            ErrorKey = new DynamicLocaleKey(LocaleKey.PromptEditor_SaveFailedToast_Title);
            return false;
        }

        Assistant.SystemPromptId = prompt.Id;
        return true;
    }

    /// <summary>
    /// Enables the configured shortcut before advancing to text-selection setup.
    /// </summary>
    public bool ConfirmShortcut()
    {
        if (!IsConnectivityChecked || Assistant.SystemPromptId == Guid.Empty || !HasValidShortcut) return false;
        Settings.Shortcut.ChatWindow.IsEnabled = true;
        return true;
    }

    /// <summary>
    /// Disables the global wake-up shortcut before advancing to text-selection setup.
    /// </summary>
    public bool SkipShortcut()
    {
        if (!IsConnectivityChecked || Assistant.SystemPromptId == Guid.Empty) return false;
        Settings.Shortcut.ChatWindow.IsEnabled = false;
        return true;
    }

    /// <summary>
    /// Enables the text-selection toolbar and records OOBE completion before advancing to the final screen.
    /// </summary>
    public Task<bool> ConfirmTextSelectionAsync(CancellationToken cancellationToken = default) =>
        CompleteTextSelectionAsync(isEnabled: true, cancellationToken);

    /// <summary>
    /// Disables the text-selection toolbar and records OOBE completion before advancing to the final screen.
    /// </summary>
    public Task<bool> SkipTextSelectionAsync(CancellationToken cancellationToken = default) =>
        CompleteTextSelectionAsync(isEnabled: false, cancellationToken);

    private async Task<bool> CompleteTextSelectionAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        if (!IsConnectivityChecked || Assistant.SystemPromptId == Guid.Empty) return false;
        cancellationToken.ThrowIfCancellationRequested();

        if (_textSelectionController is not null)
        {
            await _textSelectionController.SetToolbarEnabledCommand.ExecuteAsync(isEnabled);
            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            Settings.TextSelection.IsToolbarEnabled = isEnabled;
        }

        Settings.CompletedOobeVersion = Math.Max(Settings.CompletedOobeVersion, OobeCoordinator.CurrentVersion);
        return true;
    }

    /// <inheritdoc />
    public override void StartUsing()
    {
        Settings.Model.SelectedCustomAssistant = Assistant;
        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage());
    }

    /// <inheritdoc />
    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        OnPropertyChanged(nameof(CanLogIn));
        OnPropertyChanged(nameof(CanSelectCatalogModel));
        OnPropertyChanged(nameof(CanRefreshCatalog));
        LogInCommand.NotifyCanExecuteChanged();
        RefreshCatalogCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            _observedConfiguration.PropertyChanged -= HandleConfigurationChanged;
            _observedApiKeys.CollectionChanged -= HandleApiKeysChanged;
            Settings.Model.PropertyChanged -= HandleModelSettingsChanged;
            if (_observedApiKey is not null) _observedApiKey.PropertyChanged -= HandleApiKeyChanged;
            Assistant.PropertyChanged -= HandleAssistantChanged;
            CloudClient.PropertyChanged -= HandleCloudClientChanged;
            Settings.Shortcut.ChatWindow.PropertyChanged -= HandleShortcutChanged;
            Settings.Display.PropertyChanged -= HandleDisplaySettingsChanged;
            Personalization.PropertyChanged -= HandlePersonalizationChanged;
            foreach (var option in Personalization.ScenarioOptions)
            {
                option.PropertyChanged -= HandleScenarioOptionPropertyChanged;
            }
            if (_presetModelProvider is not null)
            {
                _presetModelProvider.CatalogChanged -= HandleCatalogChanged;
                _presetModelProvider.PropertyChanged -= HandleProviderPropertyChanged;
            }
            if (_officialModelProvider is not null)
            {
                _officialModelProvider.CatalogChanged -= HandleCatalogChanged;
                _officialModelProvider.PropertyChanged -= HandleProviderPropertyChanged;
            }
            Personalization.Dispose();
        }

        base.Dispose(disposing);
    }

    [RelayCommand]
    private void SelectSource(WelcomeModelSource source)
    {
        if (IsBusy || SelectedSource == source) return;
        SelectedSource = source;
        SwitchConfiguration(
            source == WelcomeModelSource.Cloud ? typeof(OfficialAssistantConfiguration)
            : IsAdvancedMode ? typeof(AdvancedAssistantConfiguration)
            : typeof(PresetAssistantConfiguration));
        RefreshNavigation();
    }

    internal bool IsLoginStepInProgressPath(WelcomeLoginStep step) => IsStepInProgressHistory(step);

    [RelayCommand]
    private void SelectMode(bool isAdvanced)
    {
        if (IsBusy || !IsOwnSource || IsAdvancedMode == isAdvanced) return;
        IsAdvancedMode = isAdvanced;
        SwitchConfiguration(isAdvanced ? typeof(AdvancedAssistantConfiguration) : typeof(PresetAssistantConfiguration));
    }

    [RelayCommand]
    private void SwitchToCloud()
    {
        if (IsBusy || IsCloudSource) return;
        SelectedSource = WelcomeModelSource.Cloud;
        SwitchConfiguration(typeof(OfficialAssistantConfiguration));
        ErrorKey = null;

        if (!IsSignedIn && Steps.AsValueEnumerable().OfType<WelcomeLoginStep>().FirstOrDefault() is { } loginStep)
        {
            var introStep = Steps.AsValueEnumerable().OfType<WelcomeIntroStep>().FirstOrDefault();
            var sourceStep = Steps.AsValueEnumerable().OfType<WelcomeSourceStep>().FirstOrDefault();
            JumpToStep(
                loginStep,
                [.. new OobeStep?[] { introStep, sourceStep }.OfType<OobeStep>()],
                isReversed: true);
            return;
        }

        RefreshNavigation();
    }

    internal async Task AuthenticateCloudAsync(CancellationToken cancellationToken)
    {
        await CloudClient.LoginAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSignedIn)
        {
            ErrorKey = CloudClient.LastLoginErrorKey ?? new DynamicLocaleKey(LocaleKey.Oobe_LoginFailed);
        }

        RefreshNavigation();
    }

    [RelayCommand(CanExecute = nameof(CanLogIn))]
    private Task LogInAsync() => RunAsync(AuthenticateCloudAsync);

    [RelayCommand(CanExecute = nameof(CanRefreshCatalog))]
    private Task RefreshCatalogAsync(CancellationToken cancellationToken)
    {
        ErrorKey = null;
        if (IsCloudSource && _officialModelProvider is not null)
        {
            return _officialModelProvider.RefreshAsync(this, cancellationToken);
        }

        if (IsPresetMode && _presetModelProvider is not null)
        {
            return _presetModelProvider.RefreshAsync(this, cancellationToken);
        }

        return Task.CompletedTask;
    }

    private void SwitchConfiguration(Type type)
    {
        if (!_configurationDrafts.TryGetValue(type, out var configuration))
        {
            configuration = type == typeof(OfficialAssistantConfiguration) ? AssistantSnapshotMapper.ToOfficial(Assistant.Configuration) :
                type == typeof(PresetAssistantConfiguration) ? AssistantSnapshotMapper.ToPreset(Assistant.Configuration) :
                AssistantSnapshotMapper.ToAdvanced(Assistant.Configuration);
            _configurationDrafts.Add(type, configuration);
        }

        Assistant.Configuration = configuration;
    }

    private void HandleAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Assistant.Configuration)) return;
        _observedConfiguration.PropertyChanged -= HandleConfigurationChanged;
        _observedConfiguration = Assistant.Configuration;
        _observedConfiguration.PropertyChanged += HandleConfigurationChanged;
        OnPropertyChanged(nameof(PresetConfiguration));
        OnPropertyChanged(nameof(AdvancedConfiguration));
        OnPropertyChanged(nameof(OfficialConfiguration));
        OnPropertyChanged(nameof(SelectedPresetProvider));
        OnPropertyChanged(nameof(SelectedApiKey));
        OnPropertyChanged(nameof(CanSelectCatalogModel));
        OnPropertyChanged(nameof(CanRefreshCatalog));
        OnPropertyChanged(nameof(IsCatalogRefreshing));
        RefreshCatalogCommand.NotifyCanExecuteChanged();
        ObserveSelectedApiKey();
        ReconcileCatalogSelection();
        NotifyModelCapabilitiesChanged();
        InvalidateConnectivity();
    }

    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Validation and UI projections do not alter the outgoing connection payload.
        if (e.PropertyName is nameof(AssistantConfiguration.HasErrors)
            or nameof(AssistantConfiguration.SettingsItems)
            or nameof(PresetAssistantConfiguration.ModelProviderTemplate))
        {
            return;
        }

        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isDisposed) return;
            if (e.PropertyName == nameof(AssistantConfiguration.ApiKey))
            {
                ObserveSelectedApiKey();
                OnPropertyChanged(nameof(SelectedApiKey));
            }
            if (e.PropertyName == nameof(PresetAssistantConfiguration.ProviderId))
            {
                OnPropertyChanged(nameof(SelectedPresetProvider));
                OnPropertyChanged(nameof(CanSelectCatalogModel));
                OnPropertyChanged(nameof(CanRefreshCatalog));
                RefreshCatalogCommand.NotifyCanExecuteChanged();
            }
            if (!_isApplyingCatalogSelection &&
                (e.PropertyName is nameof(PresetAssistantConfiguration.ProviderId) or nameof(AssistantConfiguration.ModelId) ||
                    _assistantCatalog is null))
            {
                ReconcileCatalogSelection();
            }
            NotifyModelCapabilitiesChanged();
            InvalidateConnectivity();
        });
    }

    private void ObserveSelectedApiKey()
    {
        var key = _observedApiKeys.FirstOrDefault(candidate => candidate.Id == Assistant.Configuration.ApiKey);
        if (ReferenceEquals(key, _observedApiKey)) return;
        if (_observedApiKey is not null) _observedApiKey.PropertyChanged -= HandleApiKeyChanged;
        _observedApiKey = key;
        if (key is not null) key.PropertyChanged += HandleApiKeyChanged;
        InvalidateConnectivity();
    }

    private void HandleApiKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApiKey.SecretKey)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_isDisposed) InvalidateConnectivity();
        });
    }

    private void HandleApiKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isDisposed) return;
            RebuildAvailableApiKeys();
            ObserveSelectedApiKey();
        });
    }

    private void HandleModelSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModelSettings.ApiKeys)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isDisposed) return;
            _observedApiKeys.CollectionChanged -= HandleApiKeysChanged;
            _observedApiKeys = Settings.Model.ApiKeys;
            _observedApiKeys.CollectionChanged += HandleApiKeysChanged;
            RebuildAvailableApiKeys();
            ObserveSelectedApiKey();
        });
    }

    private void RebuildAvailableApiKeys()
    {
        _availableApiKeys.Reset([ApiKey.Empty, .. _observedApiKeys]);
        OnPropertyChanged(nameof(SelectedApiKey));
    }

    private void HandleCatalogChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_isDisposed) ReconcileCatalogSelection();
        });
    }

    private void HandleProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IPresetModelProvider.IsRefreshing)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isDisposed) return;
            OnPropertyChanged(nameof(IsCatalogRefreshing));
            OnPropertyChanged(nameof(CanRefreshCatalog));
            RefreshCatalogCommand.NotifyCanExecuteChanged();
        });
    }

    private void ReconcileCatalogSelection()
    {
        if (_isSynchronizingCatalog || _isApplyingCatalogSelection) return;

        if (IsAdvancedOwnMode)
        {
            CatalogModels = [];
            IsSelectedCatalogModelUnlisted = false;
            SetSelectedCatalogModel(null, apply: false);
            return;
        }

        if (_assistantCatalog is not null)
        {
            var selection = _assistantCatalog.Resolve(Assistant.Configuration).PreserveItems(CatalogModels);
            _isSynchronizingCatalog = true;
            try
            {
                CatalogModels = selection.Items;
                IsSelectedCatalogModelUnlisted = IsPresetMode ? selection.SelectedItem?.IsFallback == true : selection.IsSelectedModelUnavailable;
                SetSelectedCatalogModel(selection.SelectedItem, apply: false);
            }
            finally
            {
                _isSynchronizingCatalog = false;
            }
            return;
        }

        // Without an injected catalog service (e.g. minimal headless tests), keep any explicitly configured model selectable.
        _isSynchronizingCatalog = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(Assistant.Configuration.ModelId) && (IsCloudSource || DisplayProviderTemplate is not null))
            {
                var scopeId = PresetConfiguration?.ProviderId ?? "official";
                var synthesized = new AssistantCatalogItem(
                    scopeId,
                    Assistant.Configuration.ToTemplate(),
                    Assistant.Configuration.Schema,
                    Assistant.Configuration.Endpoint ?? string.Empty,
                    IsFallback: false);
                CatalogModels = [synthesized];
                IsSelectedCatalogModelUnlisted = false;
                SetSelectedCatalogModel(synthesized, apply: false);
            }
            else
            {
                CatalogModels = [];
                IsSelectedCatalogModelUnlisted = false;
                SetSelectedCatalogModel(null, apply: false);
            }
        }
        finally
        {
            _isSynchronizingCatalog = false;
        }
    }

    private void SetSelectedCatalogModel(AssistantCatalogItem? value, bool apply)
    {
        if (_isSynchronizingCatalog && apply || value is null && apply) return;
        if (ReferenceEquals(_selectedCatalogModel, value)) return;

        _selectedCatalogModel = value;
        OnPropertyChanged(nameof(SelectedCatalogModel));
        if (!apply || value is null) return;

        _isApplyingCatalogSelection = true;
        try
        {
            lock (Assistant.Configuration)
            {
                Assistant.Configuration.Apply(value);
            }
        }
        finally
        {
            _isApplyingCatalogSelection = false;
        }

        ReconcileCatalogSelection();
        NotifyModelCapabilitiesChanged();
        InvalidateConnectivity();
    }

    private void SetInputModality(Modalities modality, bool isSupported)
    {
        var current = Assistant.Configuration.InputModalities;
        var next = isSupported ? current | modality : current & ~modality;
        if (next == current) return;
        Assistant.Configuration.InputModalities = next;
    }

    private void ApplyTokenLimit(
        string? rawValue,
        int currentValue,
        Action<AssistantConfiguration, int> setter,
        string textPropertyName,
        string formattedPropertyName
    )
    {
        int nextValue;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            nextValue = 0;
        }
        else if (int.TryParse(rawValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            nextValue = Math.Max(0, parsed);
        }
        else
        {
            OnPropertyChanged(textPropertyName);
            return;
        }

        if (nextValue != currentValue)
        {
            setter(Assistant.Configuration, nextValue);
            OnPropertyChanged(formattedPropertyName);
            OnPropertyChanged(nameof(HasTokenLimits));
        }

        OnPropertyChanged(textPropertyName);
    }

    void IExceptionHandler.HandleException(
        Exception exception,
        string? message,
        object? source,
        int lineNumber
    )
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_isDisposed)
            {
                ErrorKey = exception.GetFriendlyMessage();
            }
        });
    }

    private void InvalidateConnectivity()
    {
        _configurationRevision++;
        IsConnectivityChecked = false;
        ErrorKey = null;
        RefreshNavigation();
    }

    private void HandleCloudClientChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ICloudClient.UserProfile)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isDisposed) return;
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(CanLogIn));
            LogInCommand.NotifyCanExecuteChanged();
            if (IsCloudSource) InvalidateConnectivity();
            RefreshNavigation();
        });
    }

    /// <inheritdoc />
    protected override void OnCurrentStepChangedCore(OobeStep? step)
    {
        base.OnCurrentStepChangedCore(step);
        if (step is WelcomeConfigurationStep)
        {
            ReconcileCatalogSelection();
        }
    }

    [RelayCommand]
    private void SelectFontSize(int fontSize)
    {
        if (IsBusy) return;
        Settings.Display.FontSize = fontSize;
    }

    private void HandleShortcutChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasValidShortcut));
        RefreshNavigation();
    }

    private void HandleDisplaySettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DisplaySettings.FontSize)) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isDisposed) return;
            OnPropertyChanged(nameof(IsFontSizeSmall));
            OnPropertyChanged(nameof(IsFontSizeMedium));
            OnPropertyChanged(nameof(IsFontSizeLarge));
        });
    }

    private void HandleScenarioOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PromptRecipeOptionItem.IsSelected))
        {
            return;
        }

        _hasInteractedWithScenarios = true;
        OnPropertyChanged(nameof(HasScenarios));
        OnPropertyChanged(nameof(ShowScenarioRequiredHint));
        RefreshNavigation();
    }

    private void HandlePersonalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PromptEditorViewModel.PreviewQuestionKey) or nameof(PromptEditorViewModel.PreviewReplyKey))
        {
            OnPropertyChanged(e.PropertyName);
            return;
        }

        if (e.PropertyName is not (nameof(PromptEditorViewModel.Template)
            or nameof(PromptEditorViewModel.PreferredUserName)
            or nameof(PromptEditorViewModel.SelectedPersonaOption)
            or nameof(PromptEditorViewModel.SelectedToneOption)
            or nameof(PromptEditorViewModel.SelectedDetailOption)
            or nameof(PromptEditorViewModel.SelectedOrganizationOption)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasScenarios));
        OnPropertyChanged(nameof(ShowScenarioRequiredHint));
        RefreshNavigation();
    }

    private void NotifyModelCapabilitiesChanged()
    {
        OnPropertyChanged(nameof(DisplayModelName));
        OnPropertyChanged(nameof(HasConfiguredModel));
        OnPropertyChanged(nameof(ShowTelemetryMetrics));
        OnPropertyChanged(nameof(DisplayProviderTemplate));
        OnPropertyChanged(nameof(DisplayProviderHost));
        OnPropertyChanged(nameof(HasTelemetryHeader));
        OnPropertyChanged(nameof(EndpointPreviewText));
        OnPropertyChanged(nameof(AdvancedEndpointPlaceholder));
        OnPropertyChanged(nameof(ContextLimitText));
        OnPropertyChanged(nameof(OutputLimitText));
        OnPropertyChanged(nameof(AdvancedContextLimitText));
        OnPropertyChanged(nameof(AdvancedOutputLimitText));
        OnPropertyChanged(nameof(HasTokenLimits));
        OnPropertyChanged(nameof(SupportsTextInput));
        OnPropertyChanged(nameof(SupportsImageInput));
        OnPropertyChanged(nameof(SupportsAudioInput));
        OnPropertyChanged(nameof(SupportsVideoInput));
        OnPropertyChanged(nameof(SupportsPdfInput));
        OnPropertyChanged(nameof(SupportsTools));
        OnPropertyChanged(nameof(SupportsReasoning));
    }

    private static string? FormatTokenCount(int tokens)
    {
        return tokens switch
        {
            <= 0 => null,
            >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
            >= 1_000 => $"{tokens / 1_000d:0.#}K",
            _ => tokens.ToString()
        };
    }
}

/// <summary>
/// The explicitly selected model source. Null on a new run means no choice has been made yet.
/// </summary>
public enum WelcomeModelSource
{
    Cloud,
    Own
}
