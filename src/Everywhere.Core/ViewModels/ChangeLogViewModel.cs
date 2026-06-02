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

[JsonSerializable(typeof(List<ReleaseInfo>))]
public sealed partial class ReleaseInfoJsonSerializerContext : JsonSerializerContext;

public sealed partial class ChangeLogViewModel : BusyViewModelBase
{
    [GeneratedRegex(@"^##\s+\[(?<tag>v[0-9.]+)\]\((?<url>[^\)]+)\)\s+-\s+(?<date>\d{4}-\d{2}-\d{2})")]
    private static partial Regex VersionHeaderRegex();

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
            try
            {
                var releases = new List<ReleaseInfo>();
                await using var changeLogStream = AssetLoader.Open(new Uri("avares://Everywhere.Core/Assets/CHANGELOG.md", UriKind.Absolute));
                using var changeLogReader = new StreamReader(changeLogStream);

                ReleaseInfo? currentRelease = null;
                var currentNotes = new StringBuilder();

                while (await changeLogReader.ReadLineAsync(token) is { } line)
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
                            IsCurrent = SemanticVersion.TryParse(tag.TrimStart('v'), out var version) && version == RuntimeConstants.Version
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

                _releaseInfosSource.AddOrUpdate(releases);

                if (SelectedReleaseInfo == null && ReleaseInfos.Count > 0)
                {
                    SelectedReleaseInfo = ReleaseInfos[0];
                }
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex);
                _logger.LogError(ex, "Failed to load local changelog.");

                // ReSharper disable once PossibleIntendedRethrow
                throw ex;
            }

            try
            {
                using var httpClient = _httpClientFactory.CreateClient();
                var response = await httpClient.GetAsync("https://api.github.com/repos/Sylinko/Everywhere/releases?per_page=20", token);
                if (response.IsSuccessStatusCode)
                {
                    await using var jsonStream = await response.Content.ReadAsStreamAsync(token);
                    var githubReleases = await JsonSerializer.DeserializeAsync(
                        jsonStream,
                        ReleaseInfoJsonSerializerContext.Default.ListReleaseInfo,
                        token);
                    if (githubReleases != null)
                    {
                        var newReleases = githubReleases.Where(r => !_releaseInfosSource.Lookup(r.Tag ?? string.Empty).HasValue);
                        _releaseInfosSource.AddOrUpdate(newReleases);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException ex)
            {
                var handledException = new HandledSystemException(
                    ex,
                    HandledSystemExceptionType.Timeout,
                    new DynamicResourceKey(LocaleKey.FriendlyExceptionMessage_HttpRequest_RequestTimeout));
                _logger.LogError(handledException, "Failed to load GitHub releases.");

                throw handledException;
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex);
                _logger.LogError(ex, "Failed to load GitHub releases.");

                // ReSharper disable once PossibleIntendedRethrow
                throw ex;
            }
        },
        ToastExceptionHandler,
        cancellationToken);

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
