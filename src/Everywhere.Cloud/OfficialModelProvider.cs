using System.ComponentModel;
using System.Reactive.Disposables;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

public sealed partial class OfficialModelProvider : ModelCatalogProvider<OfficialModelDefinition[], OfficialModelDefinition[]>,
    IOfficialModelProvider, IAsyncInitializer
{
    public OfficialModelCatalog Catalog => Volatile.Read(ref _catalog);

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 1;

    [ObservableProperty]
    public partial OfficialModelCatalogAccessStatus AccessStatus { get; private set; } = OfficialModelCatalogAccessStatus.Pending;

    public event EventHandler? CatalogChanged;

    protected override int CacheVersion => 2;

    protected override bool CanRefresh =>
        _cloudClient.LoginStatus == CloudClientLoginStatus.LoggedIn &&
        !CloudConstants.AIGatewayBaseUrl.IsNullOrEmpty();

    private readonly ICloudClient _cloudClient;
    private readonly CompositeDisposable _disposables = new(1);

    private OfficialModelCatalog _catalog = OfficialModelCatalog.Empty;

    public OfficialModelProvider(
        PersistentState persistentState,
        ICloudClient cloudClient,
        IHttpClientFactory httpClientFactory,
        ILogger<OfficialModelProvider> logger
    ) : base(
        new OfficialModelCatalogClient(httpClientFactory),
        new OfficialModelCatalogCacheStore(persistentState),
        logger)
    {
        _cloudClient = cloudClient;

        cloudClient.PropertyChanged += HandleCloudClientPropertyChanged;
        _disposables.Add(Disposable.Create(() => cloudClient.PropertyChanged -= HandleCloudClientPropertyChanged));
    }

    public async Task InitializeAsync()
    {
        await RestoreCacheAsync();
        HandleCloudClientStateChanged(nameof(ICloudClient.LoginStatus));
    }

    protected override OfficialModelDefinition[] PrepareCatalog(OfficialModelDefinition[] definitions) => definitions;

    protected override void ApplyCatalog(OfficialModelDefinition[] definitions, bool isAuthoritative)
    {
        if (IsDisposed) return;

        var previous = Catalog;
        var models = ModelCatalogSnapshot<OfficialModelDefinition, string>.Create(
            definitions
                .OrderBy(static definition => definition.Model, ModelDefinitionComparer.Shared)
                .Select(static definition => KeyValuePair.Create(definition.Model.ModelId, definition)),
            previous.Models);
        Volatile.Write(ref _catalog, new OfficialModelCatalog(models, isAuthoritative));
    }

    protected override void ClearPublishedCatalog()
    {
        if (IsDisposed) return;

        Volatile.Write(ref _catalog, OfficialModelCatalog.Empty);
    }

    protected override void OnCatalogChanged()
    {
        if (IsDisposed) return;

        if (Catalog.IsAuthoritative) AccessStatus = OfficialModelCatalogAccessStatus.Available;
        OnPropertyChanged(nameof(Catalog));
        RaiseCatalogChanged(CatalogChanged);
    }

    protected override bool HandleFinalFailure(Exception exception)
    {
        if (exception is not UserNotLoginException) return false;

        AccessStatus = OfficialModelCatalogAccessStatus.SignInRequired;
        InvalidateAsync().Detach(_logger.ToExceptionHandler());
        return true;
    }

    private void HandleCloudClientPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ICloudClient.LoginStatus) or nameof(ICloudClient.Subscription))) return;
        HandleCloudClientStateChanged(e.PropertyName);
    }

    private void HandleCloudClientStateChanged(string? propertyName)
    {
        if (IsDisposed) return;

        if (propertyName == nameof(ICloudClient.Subscription))
        {
            if (_cloudClient.LoginStatus == CloudClientLoginStatus.LoggedIn) RefreshInBackground();
            return;
        }

        switch (_cloudClient.LoginStatus)
        {
            case CloudClientLoginStatus.AutoLoggingIn:
                AccessStatus = OfficialModelCatalogAccessStatus.Pending;
                break;
            case CloudClientLoginStatus.LoggedIn:
                AccessStatus = OfficialModelCatalogAccessStatus.Available;
                RefreshInBackground();
                break;
            case CloudClientLoginStatus.NotLoggedIn:
            case CloudClientLoginStatus.LoginFailed:
                AccessStatus = OfficialModelCatalogAccessStatus.SignInRequired;
                InvalidateAsync().Detach(_logger.ToExceptionHandler());
                break;
            default:
                _logger.LogWarning("Unexpected login status: {LoginStatus}", _cloudClient.LoginStatus);
                break;
        }
    }

    protected override void DisposeCore()
    {
        _disposables.Dispose();
    }
}