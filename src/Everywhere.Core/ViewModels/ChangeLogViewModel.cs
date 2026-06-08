using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.Logging;

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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChangeLogViewModel> _logger;
    private readonly SourceCache<ReleaseInfo, string> _releaseInfosSource = new(r => r.Tag ?? string.Empty);

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
                    .Descending(r => r.PublishedDate)
                    .ThenByDescending(r => SemanticVersion.TryParse(r.Tag?.TrimStart('v'), out var v) ? v : new SemanticVersion(0))
            )
            .Subscribe()
            .AddTo(LifetimeDisposables);
        ReleaseInfos = releaseInfos.ToReadOnlyBindableList();
        LifetimeDisposables.Add(_releaseInfosSource);
    }

    protected internal override Task ViewLoaded(CancellationToken cancellationToken) => ExecuteBusyTaskAsync(
        async token =>
        {
            var updateChannel = SoftwareUpdater.UpdateChannel;
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

                    // ReSharper disable once PossibleIntendedRethrow
                    throw ex;
                }
            }

            try
            {
                var releases = await LoadRemoteVersionHistoryAsync(updateChannel, token);
                _releaseInfosSource.AddOrUpdate(releases);
                SelectFirstReleaseInfo();
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException ex)
            {
                var handledException = new HandledSystemException(
                    ex,
                    HandledSystemExceptionType.Timeout,
                    new DynamicResourceKey(LocaleKey.FriendlyExceptionMessage_HttpRequest_RequestTimeout));
                _logger.LogError(handledException, "Failed to load version history.");

                throw handledException;
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex);
                _logger.LogError(ex, "Failed to load version history.");

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
        var response = await httpClient.GetAsync(
            $"{UpdateServiceBaseUrl}/versions?product=everywhere&channel={channelName}&limit={VersionHistoryLimit}",
            cancellationToken);
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
            .Where(r =>
                r.Channel?.Equals(channelName, StringComparison.OrdinalIgnoreCase) is true &&
                !r.Version.IsNullOrWhiteSpace() &&
                !r.TagName.IsNullOrWhiteSpace() &&
                r.PublishedAt != default &&
                r.ReleaseUrl != null)
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

    [RelayCommand]
    private async Task PerformUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SoftwareUpdater.LatestUpdate is not { IsReady: true })
            {
                var progress = new Progress<double>();
                var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ToastHost
                    .CreateToast(LocaleResolver.Common_Info)
                    .WithContent(LocaleResolver.CommonSettings_SoftwareUpdate_Toast_DownloadingUpdate)
                    .WithProgress(progress)
                    .WithCancellationTokenSource(cancellationTokenSource)
                    .OnBottomRight()
                    .ShowInfo();
                await SoftwareUpdater.PerformUpdateAsync(progress, cancellationTokenSource.Token);
            }
            else
            {
                await SoftwareUpdater.PerformUpdateAsync(cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform update.");

            ex = new HandledException(ex, new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_UpdateFailed_Content));
            ToastExceptionHandler.HandleException(ex);
        }
    }
}
