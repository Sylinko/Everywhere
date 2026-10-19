using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Common;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.ViewModels;

public sealed class ReleaseInfo
{
    [JsonPropertyName("published_at")]
    public DateTimeOffset PublishedDate { get; set; }

    [JsonPropertyName("tag_name")]
    public string? Tag { get; set; }

    [JsonPropertyName("html_url")]
    public Uri? HtmlUrl { get; set; }

    [JsonPropertyName("body")]
    public string? ReleaseNotes { get; set; }

    [JsonIgnore]
    public bool IsCurrent { get; set; }

    [JsonIgnore]
    [field: AllowNull, MaybeNull]
    public ObservableStringBuilder MarkdownBuilder => field ??= new ObservableStringBuilder().Append(ReleaseNotes);
}

public sealed class VersionHistoryResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    // [JsonPropertyName("latestVersion")]
    // public string? LatestVersion { get; set; }

    // [JsonPropertyName("generatedAt")]
    // public DateTimeOffset GeneratedAt { get; set; }

    // [JsonPropertyName("limit")]
    // public int Limit { get; set; }

    [JsonPropertyName("releases")]
    public List<VersionHistoryRelease> Releases { get; set; } = [];
}

public sealed class VersionHistoryRelease
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("tagName")]
    public string? TagName { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; set; }

    // [JsonPropertyName("releaseId")]
    // public long? ReleaseId { get; set; }

    [JsonPropertyName("releaseUrl")]
    public Uri? ReleaseUrl { get; set; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    // [JsonPropertyName("isLatest")]
    // public bool IsLatest { get; set; }

    // [JsonPropertyName("assetCount")]
    // public int AssetCount { get; set; }
}

public enum UpdatePageAction
{
    Hidden,
    DownloadAndInstall,
    Install,
    InstallFallback,
    Retry,
    Cancel
}

[JsonSerializable(typeof(VersionHistoryResponse))]
[JsonSerializable(typeof(List<ReleaseInfo>))]
public sealed partial class ChangeLogViewModelJsonSerializerContext : JsonSerializerContext;

public sealed partial class ChangeLogViewModel : BusyViewModelBase
{
    [GeneratedRegex(@"^##\s+\[(?<tag>v[0-9.]+)\]\((?<url>[^\)]+)\)\s+-\s+(?<date>\d{4}-\d{2}-\d{2})")]
    private static partial Regex VersionHeaderRegex();

    private const string UpdateServiceBaseUrl = "https://download.sylinko.com";
    private const int VersionHistoryLimit = 50;

    public ISoftwareUpdater SoftwareUpdater { get; }

    public IReadOnlyBindableList<ReleaseInfo> ReleaseInfos { get; }

    [ObservableProperty]
    public partial ReleaseInfo? SelectedReleaseInfo { get; set; }

    /// <summary>
    /// Gets the action projected from page discovery and the shared operation, without a second workflow state.
    /// </summary>
    public UpdatePageAction UpdateAction
    {
        get
        {
            if (IsBusy) return UpdatePageAction.Hidden;
            if (SoftwareUpdater.CurrentOperation is { IsActive: true } operation)
                return operation.CanCancel ? UpdatePageAction.Cancel : UpdatePageAction.Hidden;
            if (_resolvedAction is UpdatePageAction.Install or UpdatePageAction.DownloadAndInstall)
                return SoftwareUpdater.ReadyUpdate?.HasSamePackageIdentity(_resolvedTarget) == true
                    ? UpdatePageAction.Install : UpdatePageAction.DownloadAndInstall;
            return _resolvedAction;
        }
    }

    /// <summary>
    /// Gets the active operation's target, or the release resolved by this page.
    /// </summary>
    public SoftwareUpdateMetadata? UpdateTarget
    {
        get
        {
            if (IsBusy) return null;
            return SoftwareUpdater.CurrentOperation is { IsActive: true, Target: { } target } ? target : _resolvedTarget;
        }
    }

    public bool IsUpdateButtonVisible => UpdateAction != UpdatePageAction.Hidden;

    /// <summary>
    /// Gets the live-localized action label, including the selected version where applicable.
    /// </summary>
    public IDynamicLocaleKey UpdateButtonContentKey => UpdateAction switch
    {
        UpdatePageAction.DownloadAndInstall => new FormattedDynamicLocaleKey(LocaleKey.ChangeLogView_DownloadAndInstallButton_Content, UpdateTarget?.Version),
        UpdatePageAction.Install => new FormattedDynamicLocaleKey(LocaleKey.ChangeLogView_InstallButton_Content, UpdateTarget?.Version),
        UpdatePageAction.InstallFallback => new FormattedDynamicLocaleKey(LocaleKey.ChangeLogView_InstallFallbackButton_Content, UpdateTarget?.Version),
        UpdatePageAction.Retry => new DynamicLocaleKey(LocaleKey.Common_Retry),
        UpdatePageAction.Cancel => new DynamicLocaleKey(LocaleKey.Common_Cancel),
        _ => new DynamicLocaleKey(LocaleKey.ChangeLogView_UpdateButton_Content)
    };

    /// <summary>
    /// Gets whether shared update work is currently running, independently of page refreshes.
    /// </summary>
    public bool IsUpdateInProgress => SoftwareUpdater.CurrentOperation is { IsActive: true };

    /// <summary>
    /// Gets whether the current update stage has no measurable progress.
    /// </summary>
    public bool IsUpdateProgressIndeterminate => SoftwareUpdater.CurrentOperation?.IsIndeterminate != false;

    /// <summary>
    /// Gets normalized download progress for the page's progress bar.
    /// </summary>
    public double UpdateProgress
    {
        get
        {
            var progress = SoftwareUpdater.CurrentOperation?.Progress ?? 0d;
            return double.IsFinite(progress) ? Math.Clamp(progress, 0d, 1d) : 0d;
        }
    }

    /// <summary>
    /// Gets the localized status of the active operation; views resolve the key at presentation time.
    /// </summary>
    public IDynamicLocaleKey UpdateStatusKey => SoftwareUpdater.CurrentOperation?.Stage switch
    {
        SoftwareUpdateOperationStage.Checking => new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_CheckingUpdateTitle_Text),
        SoftwareUpdateOperationStage.Downloading => new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_DownloadingUpdate),
        SoftwareUpdateOperationStage.Validating => new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_ValidatingUpdate),
        SoftwareUpdateOperationStage.Cancelling => new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_CancellingUpdate),
        SoftwareUpdateOperationStage.Installing => new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_UpdatingTitle_Text),
        _ => DirectLocaleKey.Empty
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChangeLogViewModel> _logger;
    private readonly SourceCache<ReleaseInfo, string> _releaseInfosSource = new(r => r.Tag ?? string.Empty);
    private UpdatePageAction _resolvedAction;
    private SoftwareUpdateMetadata? _resolvedTarget;

    public ChangeLogViewModel(ISoftwareUpdater softwareUpdater, IHttpClientFactory httpClientFactory, ILogger<ChangeLogViewModel> logger)
    {
        SoftwareUpdater = softwareUpdater;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _releaseInfosSource.Connect()
            .ObserveOnAvaloniaDispatcher()
            .SortAndBind(
                out var releaseInfos,
                SortExpressionComparer<ReleaseInfo>
                    .Descending(r => SemanticVersion.TryParse(r.Tag?.TrimStart('v', 'V'), out var v) ? v : new SemanticVersion(0))
                    .ThenByDescending(r => r.PublishedDate)
            )
            .Subscribe()
            .AddTo(LifetimeDisposables);
        ReleaseInfos = releaseInfos.ToReadOnlyBindableList();
        LifetimeDisposables.Add(_releaseInfosSource);

        softwareUpdater.PropertyChanged += HandleSoftwareUpdaterPropertyChanged;
        LifetimeDisposables.Add(Disposable.Create(() => softwareUpdater.PropertyChanged -= HandleSoftwareUpdaterPropertyChanged));
    }

    protected internal override Task ViewLoaded(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    protected override void OnIsBusyChanged() => UpdateProjectedAction();

    private Task RefreshAsync(CancellationToken cancellationToken) => ExecuteBusyTaskAsync(
        async token =>
        {
            var updateChannel = SoftwareUpdater.UpdateChannel;
            SetResolvedAction(UpdatePageAction.Hidden, null);
            _releaseInfosSource.Clear();
            SelectedReleaseInfo = null;

            if (updateChannel == UpdateChannel.Stable)
            {
                try
                {
                    var releases = await LoadLocalChangeLogAsync(token);
                    _releaseInfosSource.AddOrUpdate(releases);
                    SelectFirstReleaseInfo();
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    _logger.LogError(ex, "Failed to load local changelog.");
                    SetRefreshFailureAction();

                    // ReSharper disable once PossibleIntendedRethrow
                    throw ex;
                }
            }

            try
            {
                // Explicit navigation must expose the cached fallback even without startup recovery.
                await SoftwareUpdater.RecoverCachedPackageAsync(token);
                var releases = (await LoadRemoteVersionHistoryAsync(updateChannel, token)).ToArray();
                _releaseInfosSource.AddOrUpdate(releases);
                SelectFirstReleaseInfo();

                var newestRelease = releases
                    .AsValueEnumerable()
                    .Select(release => new
                    {
                        Release = release,
                        Version = SemanticVersion.TryParse(release.Tag?.TrimStart('v', 'V'), out var version) ? version : null
                    })
                    .Where(item => item.Version is not null && item.Version > RuntimeConstants.Version)
                    .OrderByDescending(item => item.Version)
                    .FirstOrDefault();
                if (newestRelease?.Release.Tag is not { } newestTag)
                {
                    SetResolvedAction(UpdatePageAction.Hidden, null);
                    return;
                }

                var resolvedUpdate = await SoftwareUpdater.ResolveUpdateAsync(updateChannel, newestTag, token);
                var action = SoftwareUpdater.ReadyUpdate?.HasSamePackageIdentity(resolvedUpdate) == true ?
                    UpdatePageAction.Install :
                    UpdatePageAction.DownloadAndInstall;
                SetResolvedAction(action, resolvedUpdate);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (SoftwareUpdateConsistencyException ex)
            {
                _logger.LogError(ex, "Version history and fixed release details are inconsistent.");
                SetResolvedAction(UpdatePageAction.Retry, null);
                throw;
            }
            catch (TimeoutException ex)
            {
                var handledException = new HandledSystemException(
                    ex,
                    HandledSystemExceptionType.Timeout,
                    new DynamicLocaleKey(LocaleKey.FriendlyExceptionMessage_HttpRequest_RequestTimeout));
                _logger.LogError(handledException, "Failed to load version history.");
                SetRefreshFailureAction();
                throw handledException;
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex);
                _logger.LogError(ex, "Failed to load version history.");
                SetRefreshFailureAction();
                // ReSharper disable once PossibleIntendedRethrow
                throw ex;
            }
        },
        ToastExceptionHandler,
        cancellationToken);

    private async static Task<List<ReleaseInfo>> LoadLocalChangeLogAsync(CancellationToken cancellationToken)
    {
        var releases = new List<ReleaseInfo>();
        await using var changeLogStream = AssetLoader.Open(new Uri("avares://Everywhere.Core/Assets/CHANGELOG.md", UriKind.Absolute));
        using var changeLogReader = new StreamReader(changeLogStream);

        ReleaseInfo? currentRelease = null;
        var currentNotes = new StringBuilder();

        while (await changeLogReader.ReadLineAsync(cancellationToken) is { } line)
        {
            var match = VersionHeaderRegex().Match(line);
            if (match.Success)
            {
                if (currentRelease != null)
                {
                    currentRelease.ReleaseNotes = currentNotes.ToString().Trim();
                    releases.Add(currentRelease);
                }

                var tag = match.Groups["tag"].Value; // e.g. v0.7.0
                currentRelease = new ReleaseInfo
                {
                    Tag = tag,
                    HtmlUrl = new Uri(match.Groups["url"].Value, UriKind.RelativeOrAbsolute),
                    PublishedDate = DateTimeOffset.Parse(match.Groups["date"].Value),
                    IsCurrent = IsCurrentVersion(tag)
                };
                currentNotes.Clear();
            }
            else if (currentRelease != null)
            {
                currentNotes.AppendLine(line);
            }
        }

        if (currentRelease != null)
        {
            currentRelease.ReleaseNotes = currentNotes.ToString().Trim();
            releases.Add(currentRelease);
        }

        return releases;
    }

    private async Task<IEnumerable<ReleaseInfo>> LoadRemoteVersionHistoryAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        var channelName = channel.ToString();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{UpdateServiceBaseUrl}/versions?product=everywhere&channel={channelName}&limit={VersionHistoryLimit}");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var versionHistory = await JsonSerializer.DeserializeAsync(
            jsonStream,
            ChangeLogViewModelJsonSerializerContext.Default.VersionHistoryResponse,
            cancellationToken);

        if (versionHistory is not { SchemaVersion: 1 })
            throw new InvalidDataException("Unsupported version history response.");

        if (!string.Equals(versionHistory.Product, "everywhere", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected version history product.");

        if (!string.Equals(versionHistory.Channel, channelName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected version history channel.");

        if (versionHistory.Releases is null)
            throw new InvalidDataException("Version history releases field is missing.");

        return versionHistory.Releases
            .Where(release =>
                release.Channel?.Equals(channelName, StringComparison.OrdinalIgnoreCase) is true &&
                !release.Version.IsNullOrWhiteSpace() &&
                !release.TagName.IsNullOrWhiteSpace() &&
                release.PublishedAt != default &&
                release.ReleaseUrl is not null &&
                SemanticVersion.TryParse(release.Version, out var version) &&
                SemanticVersion.TryParse(release.TagName.TrimStart('v', 'V'), out var tagVersion) &&
                version == tagVersion &&
                version.Channel == channel)
            .Select(r => new ReleaseInfo
            {
                Tag = r.TagName,
                HtmlUrl = r.ReleaseUrl,
                PublishedDate = r.PublishedAt,
                ReleaseNotes = r.ReleaseNotes,
                IsCurrent = IsCurrentVersion(r.TagName)
            });
    }

    private static bool IsCurrentVersion(string? tag)
    {
        return SemanticVersion.TryParse(tag?.TrimStart('v', 'V'), out var version) && version == RuntimeConstants.Version;
    }

    private void SelectFirstReleaseInfo()
    {
        SelectedReleaseInfo = ReleaseInfos.Count > 0 ? ReleaseInfos[0] : null;
    }

    private void HandleSoftwareUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ISoftwareUpdater.CurrentOperation) or nameof(ISoftwareUpdater.ReadyUpdate))) return;

        Dispatcher.UIThread.PostOnDemand(() =>
        {
            OnPropertyChanged(nameof(IsUpdateInProgress));
            OnPropertyChanged(nameof(IsUpdateProgressIndeterminate));
            OnPropertyChanged(nameof(UpdateProgress));
            OnPropertyChanged(nameof(UpdateStatusKey));
            UpdateProjectedAction();
        });
    }

    private void SetRefreshFailureAction()
    {
        var readyUpdate = SoftwareUpdater.ReadyUpdate;
        SetResolvedAction(
            readyUpdate is not null && readyUpdate.Version > RuntimeConstants.Version ? UpdatePageAction.InstallFallback : UpdatePageAction.Retry,
            readyUpdate);
    }

    private void SetResolvedAction(UpdatePageAction action, SoftwareUpdateMetadata? target)
    {
        _resolvedAction = action;
        _resolvedTarget = target;
        UpdateProjectedAction();
    }

    private void UpdateProjectedAction()
    {
        OnPropertyChanged(nameof(UpdateAction));
        OnPropertyChanged(nameof(UpdateTarget));
        OnPropertyChanged(nameof(IsUpdateButtonVisible));
        OnPropertyChanged(nameof(UpdateButtonContentKey));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PerformUpdateAsync(CancellationToken cancellationToken)
    {
        if (UpdateAction == UpdatePageAction.Cancel)
        {
            SoftwareUpdater.CancelCurrentOperation();
            return;
        }

        if (UpdateAction == UpdatePageAction.Retry)
        {
            await RefreshAsync(cancellationToken);
            return;
        }

        var target = UpdateTarget;
        if (target is null) return;

        try
        {
            await SoftwareUpdater.PerformUpdateAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform update.");
            ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage());
        }
    }
}