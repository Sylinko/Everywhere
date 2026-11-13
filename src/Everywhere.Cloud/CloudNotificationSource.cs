using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;
using ShadUI.Extensions;

namespace Everywhere.Cloud;

/// <summary>
/// Fetches complete announcement snapshots for the current identity and application language.
/// Only acknowledgment IDs persist; a failed refresh never becomes an authoritative empty feed.
/// </summary>
public sealed partial class CloudNotificationSource(
    IHttpClientFactory httpClientFactory,
    NotificationCenter notificationCenter,
    ICloudClient cloudClient,
    ILogger<CloudNotificationSource> logger
) : IAsyncInitializer, IDisposable
{
    /// <inheritdoc />
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    /// <summary>
    /// Names the unauthenticated transport; guest requests must not enter token refresh.
    /// </summary>
    public const string AnonymousHttpClientName = "CloudNotificationSource.Anonymous";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _refreshCancellation;
    private string? _accountId;
    private DateTimeOffset _lastAttempt;
    private (EntityTagHeaderValue Tag, LocaleName Locale)? _entityTag;
    private bool _isDisposed;

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        notificationCenter.PopupWindowActivated += HandleWindowActivated;
        cloudClient.PropertyChanged += HandleAccountChanged;
        UpdateAccount();
        return Task.CompletedTask;
    }

    private void HandleWindowActivated(object? sender, EventArgs e) => UpdateAccount();

    private void HandleAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ICloudClient.UserProfile) or nameof(ICloudClient.LoginStatus))
        {
            Dispatcher.UIThread.PostOnDemand(UpdateAccount);
        }
    }

    private void UpdateAccount()
    {
        if (_isDisposed || cloudClient.LoginStatus == CloudClientLoginStatus.AutoLoggingIn)
        {
            return;
        }

        // Profile and login status are published separately. Ignore the intermediate combination;
        // neither subscription loading nor a profile replacement changes the authenticated identity.
        if (cloudClient is { LoginStatus: CloudClientLoginStatus.LoggedIn, UserProfile: null })
        {
            return;
        }

        var accountId = cloudClient.LoginStatus == CloudClientLoginStatus.LoggedIn ? cloudClient.UserProfile?.Subject : null;
        if (_accountId != accountId)
        {
            CancelRefresh();
            if (_accountId is { } previousAccount)
            {
                notificationCenter.ReplacePopups(GetAccountSource(previousAccount), []);
            }

            _accountId = accountId;
            _entityTag = null;
            _lastAttempt = default;
        }

        StartRefresh();
    }

    private void StartRefresh()
    {
        // All state and continuations stay on the UI thread. The cancellation source is also the
        // single-flight slot; account changes release it without waiting for the previous request.
        if (_isDisposed || _refreshCancellation is not null ||
            DateTimeOffset.UtcNow - _lastAttempt < RefreshInterval)
        {
            return;
        }

        _lastAttempt = DateTimeOffset.UtcNow;
        var source = new CancellationTokenSource();
        _refreshCancellation = source;
        RefreshAsync(source, _accountId, LocaleManager.CurrentLocale).Detach(logger.ToExceptionHandler());
    }

    private async Task RefreshAsync(CancellationTokenSource source, string? accountId, LocaleName locale)
    {
        var cancellationToken = source.Token;
        try
        {
            var clientName = accountId is null ? AnonymousHttpClientName : nameof(CloudNotificationSource);
            using var client = httpClientFactory.CreateClient(clientName);
            // A source-only/local build has no cloud address. Keep local notifications usable.
            if (client.BaseAddress is null) return;

            using var request = new HttpRequestMessage(HttpMethod.Get, "notifications/v1");
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(locale.ToCultureInfo().Name));
            // Language is sampled for each ordinary request, not a refresh trigger. A validator
            // from another language cannot stand in for the snapshot being requested now.
            if (_entityTag is { } cached && cached.Locale == locale) request.Headers.IfNoneMatch.Add(cached.Tag);

            using var response = await client.SendAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotModified) return;
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var feed = JsonSerializer.Deserialize(bytes, NotificationJsonSerializerContext.Default.NotificationFeed)
                ?? throw new JsonException("The notification feed is null.");
            if (feed.Items is null) throw new JsonException("The notification feed has no items array.");

            var deviceNotifications = new List<PopupNotification>();
            var accountNotifications = new List<PopupNotification>();
            foreach (var item in feed.Items)
            {
                try
                {
                    var destination = item?.Scope switch
                    {
                        null or "device" => deviceNotifications,
                        "account" => accountNotifications,
                        _ => throw new JsonException("Unsupported notification scope.")
                    };
                    destination.Add(ConvertNotification(item));
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Skipped invalid notification {NotificationId}.", item?.Id);
                }
            }

            // Keep the existing device source identity. Account ownership belongs here, not in the
            // notification center; its source key also isolates acknowledgment across local accounts.
            notificationCenter.ReplacePopups("cloud", deviceNotifications);
            if (accountId is not null)
            {
                notificationCenter.ReplacePopups(GetAccountSource(accountId), accountNotifications);
            }

            _entityTag = response.Headers.ETag is { } tag ? (tag, locale) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not refresh notifications.");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, source)) _refreshCancellation = null;
            source.Dispose();
        }
    }

    private void CancelRefresh()
    {
        var previous = _refreshCancellation;
        _refreshCancellation = null;
        // The request owns disposal in finally. Invalidate its slot before cancellation callbacks run.
        previous?.Cancel();
    }

    private static string GetAccountSource(string accountId) => $"cloud:account:{accountId}";

    private static PopupNotification ConvertNotification(RemoteNotification? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Title is not { Count: > 0 } ||
            item.Content?.Text is not { Count: > 0 } || item.Windows is not { Length: > 0 })
        {
            throw new JsonException("A notification is missing its identity, localized content, or target windows.");
        }

        var content = item.Content.Format switch
        {
            "markdown" => (NotificationContent)new MarkdownNotificationContent(item.Content.Text),
            "html" => new HtmlNotificationContent(item.Content.Text),
            _ => throw new JsonException("Unsupported notification content format.")
        };
        var windows = item.Windows.Aggregate(
            (NotificationWindows)0,
            (value, window) => value | (window switch
            {
                "main" => NotificationWindows.Main,
                "chat" => NotificationWindows.Chat,
                _ => 0
            }));
        if (windows == 0) throw new JsonException("No supported notification window.");

        return new PopupNotification
        {
            Id = item.Id,
            TitleKey = item.Title,
            AcknowledgeKey = (IDynamicLocaleKey?)item.AcknowledgeText ?? new DynamicLocaleKey(Core.I18N.LocaleKey.Common_OK),
            Windows = windows,
            Repeat = item.Repeat switch
            {
                "once" => NotificationRepeat.Once,
                "per-session" => NotificationRepeat.PerSession,
                _ => throw new JsonException("Unsupported notification repeat policy.")
            },
            ExpiresAt = item.ExpiresAt,
            Content = content
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        notificationCenter.PopupWindowActivated -= HandleWindowActivated;
        _isDisposed = true;
        cloudClient.PropertyChanged -= HandleAccountChanged;
        CancelRefresh();
    }

    private sealed record NotificationFeed(RemoteNotification?[]? Items);

    private sealed record RemoteContent(string Format, JsonDynamicLocaleKey? Text);

    private sealed record RemoteNotification(
        string? Id,
        JsonDynamicLocaleKey? Title,
        RemoteContent? Content,
        string[]? Windows,
        string? Repeat,
        DateTimeOffset? ExpiresAt,
        JsonDynamicLocaleKey? AcknowledgeText,
        string? Scope
    );

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(NotificationFeed))]
    private sealed partial class NotificationJsonSerializerContext : JsonSerializerContext;
}