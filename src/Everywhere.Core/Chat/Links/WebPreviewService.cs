using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Everywhere.Chat.Links;

public sealed record WebPreview(string? Title, string? Description, byte[]? Cover, byte[]? Icon, bool IsUnavailable);

/// <summary>
/// Owns the chat preview client, a bounded in-memory cache, and coalesced requests.
/// Clearing a hover cancels its wait, not another waiter for the same URL.
/// </summary>
public sealed partial class WebPreviewService(IHttpClientFactory clients, ILogger<WebPreviewService> logger) : IDisposable
{
    public const string HttpClientName = "ChatLinkPreview";

    private const int MaxCoverBytes = 2 * 1024 * 1024;
    private const int MaxIconBytes = 128 * 1024;
    private const int MaxConcurrentPreviews = 2;
    private const int CacheCapacity = 12;
    private const int SiteIconCacheCapacity = 128;
    private const int MaxRedirects = 3;

    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MediaBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RootIconBudget = TimeSpan.FromSeconds(4);

    private readonly HttpClient _linkPreviewClient = clients.CreateClient(HttpClientName);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PreviewCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingPreview> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<Uri, Task<SiteIcon>> _siteIcons = new();
    private readonly SemaphoreSlim _siteIconSlots = new(4);

    private bool _isDisposed;

    // A query can identify a different resource or carry credentials. Do not fetch a rewritten URL.
    public static bool CanPreviewPage(Uri address) => LinkPreviewTransport.IsAllowed(address) && address.Query.Length == 0;

    /// <summary>
    /// Shares an origin's root favicon across visible links and hover previews.
    /// </summary>
    public async Task<byte[]?> GetSiteFaviconAsync(Uri page, CancellationToken cancellationToken)
    {
        if (!CanPreviewPage(page)) return null;
        cancellationToken.ThrowIfCancellationRequested();

        var address = new Uri(page, "/favicon.ico");
        Task<SiteIcon>? fetch;
        lock (_gate)
        {
            fetch = FindSiteIcon(address);
        }

        if (fetch is null)
        {
            await _siteIconSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                fetch = FindSiteIcon(address);
                if (fetch is not null || _isDisposed || cancellationToken.IsCancellationRequested)
                {
                    _siteIconSlots.Release();
                    if (fetch is null) return null;
                }
                else
                {
                    if (_siteIcons.Count >= SiteIconCacheCapacity)
                    {
                        var oldest = _siteIcons.First(entry => entry.Value.IsCompletedSuccessfully).Key;
                        _siteIcons.Remove(oldest);
                    }

                    var token = _lifetime.Token;
                    fetch = Task.Run(() => FetchSiteFaviconAsync(address, token), CancellationToken.None);
                    _siteIcons.Add(address, fetch);
                }
            }
        }

        return (await fetch.WaitAsync(cancellationToken).ConfigureAwait(false)).Bytes;
    }

    private Task<SiteIcon>? FindSiteIcon(Uri address)
    {
        if (!_siteIcons.TryGetValue(address, out var fetch)) return null;
        if (!fetch.IsCompletedSuccessfully || fetch.Result.ExpiresAt > DateTimeOffset.UtcNow) return fetch;
        _siteIcons.Remove(address);
        return null;
    }

    private async Task<SiteIcon> FetchSiteFaviconAsync(Uri address, CancellationToken token)
    {
        try
        {
            var bytes = await FetchImageAsync(address, isIcon: true, RootIconBudget, token, shouldStayOnOrigin: true).ConfigureAwait(false);
            return new SiteIcon(bytes, DateTimeOffset.UtcNow + (bytes is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(30)));
        }
        catch (Exception exception)
        {
            LogWebPreviewProcessingFailedErrorType(exception.GetType().Name);
            return new SiteIcon(null, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30));
        }
        finally
        {
            _siteIconSlots.Release();
        }
    }

    public Task<WebPreview> GetAsync(Uri address, CancellationToken cancellationToken, IProgress<WebPreview>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanPreviewPage(address))
        {
            return Task.FromResult(Unavailable("Previews are limited to public web addresses without credentials or a query."));
        }

        // Omit the fragment without changing the path.
        var uri = new Uri(address.GetLeftPart(UriPartial.Path));
        var key = uri.AbsoluteUri;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return Task.FromResult(entry.Preview);
            }

            if (_pending.TryGetValue(key, out var pending))
            {
                return WaitAsync(pending, progress, cancellationToken);
            }

            if (_pending.Count >= MaxConcurrentPreviews)
            {
                return Task.FromResult(Unavailable("Other previews are loading. Try again in a moment."));
            }

            var lifetimeToken = _lifetime.Token;
            var stages = new TaskCompletionSource<PreviewStages?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = new PendingPreview(stages.Task, Task.Run(() => FetchAndCacheAsync(uri, key, stages, lifetimeToken), CancellationToken.None));
            _pending.Add(key, pending);
            return WaitAsync(pending, progress, cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _cache.Clear();
            _siteIcons.Clear();
            _lifetime.Cancel();
            _linkPreviewClient.Dispose();
            _lifetime.Dispose();
        }
    }

    private static async Task<WebPreview> WaitAsync(PendingPreview pending, IProgress<WebPreview>? progress, CancellationToken cancellationToken)
    {
        if (progress is not null && await pending.Stages.WaitAsync(cancellationToken).ConfigureAwait(false) is { } stages)
        {
            progress.Report(stages.Text);

            // Publish whichever asset finishes first. The final result carries both assets.
            var first = await Task.WhenAny(stages.Icon, stages.Cover).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (first is { IsCompletedSuccessfully: true, Result: { } bytes })
            {
                progress.Report(first == stages.Icon ? stages.Text with { Icon = bytes } : stages.Text with { Cover = bytes });
            }
        }

        return await pending.Complete.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WebPreview> FetchAndCacheAsync(
        Uri uri,
        string key,
        TaskCompletionSource<PreviewStages?> stages,
        CancellationToken lifetimeToken)
    {
        WebPreview result;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        budget.CancelAfter(RequestBudget);
        try
        {
            var (pageAddress, metadata) = await FetchAsync(uri, isHtml: true, WebPreviewMetadata.ReadAsync, budget.Token).ConfigureAwait(false);
            var text = new WebPreview(metadata.Title, metadata.Description, null, null, false);
            var iconTask = FetchIconAsync(pageAddress, metadata.Icon, budget.Token);
            var coverTask = FetchImageAsync(metadata.Cover, isIcon: false, MediaBudget, budget.Token);
            stages.TrySetResult(new PreviewStages(text, iconTask, coverTask));
            await Task.WhenAll(iconTask, coverTask).ConfigureAwait(false);
            var icon = await iconTask.ConfigureAwait(false);
            var cover = await coverTask.ConfigureAwait(false);
            result = text with { Cover = cover, Icon = icon };
        }
        catch (OperationCanceledException)
        {
            result = Unavailable("Preview loading timed out.");
        }
        catch (PreviewRejectedException exception)
        {
            result = Unavailable(exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException)
        {
            result = Unavailable("Could not load this page’s preview.");
        }
        catch (Exception exception)
        {
            LogWebPreviewProcessingFailedErrorType(exception.GetType().Name);
            result = Unavailable("Could not read this page’s preview.");
        }
        finally
        {
            stages.TrySetResult(null);
        }

        lock (_gate)
        {
            _pending.Remove(key);
            if (!_isDisposed)
            {
                if (!_cache.ContainsKey(key) && _cache.Count >= CacheCapacity)
                {
                    _cache.Remove(_cache.MinBy(pair => pair.Value.ExpiresAt).Key);
                }

                var ttl = result.IsUnavailable ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10);
                _cache[key] = new PreviewCacheEntry(result, DateTimeOffset.UtcNow + ttl);
            }
        }

        return result;
    }

    private async Task<byte[]?> FetchIconAsync(Uri pageAddress, Uri? declaredIcon, CancellationToken cancellationToken)
    {
        var fallback = new Uri(pageAddress, "/favicon.ico");
        if (declaredIcon is not null && declaredIcon != fallback)
        {
            var icon = await FetchImageAsync(declaredIcon, isIcon: true, MediaBudget, cancellationToken).ConfigureAwait(false);
            if (icon is not null)
            {
                return icon;
            }
        }

        return await GetSiteFaviconAsync(pageAddress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> FetchImageAsync(
        Uri? address,
        bool isIcon,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool shouldStayOnOrigin = false)
    {
        if (address is null)
        {
            return null;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            var byteLimit = isIcon ? MaxIconBytes : MaxCoverBytes;
            var image = await FetchAsync(
                address,
                isHtml: false,
                (_, content, token) => ReadImageAsync(content, byteLimit, token),
                budget.Token,
                shouldStayOnOrigin).ConfigureAwait(false);
            return DecodeRaster(image.Body, isIcon);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            // A missing or rejected image must not discard the page metadata.
            return null;
        }
    }

    private async Task<(Uri Address, T Body)> FetchAsync<T>(
        Uri uri,
        bool isHtml,
        Func<Uri, HttpContent, CancellationToken, Task<T>> readBody,
        CancellationToken cancellationToken,
        bool shouldStayOnOrigin = false)
    {
        var origin = shouldStayOnOrigin ? uri.GetLeftPart(UriPartial.Authority) : null;
        for (var hop = 0;; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LinkPreviewTransport.IsAllowed(uri))
            {
                throw new PreviewRejectedException("A preview request targeted a restricted address.");
            }

            await LinkPreviewTransport.CheckPublicTargetAsync(uri, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Everywhere-LinkPreview", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(isHtml ? "text/html" : "image/*"));
            using var response = await _linkPreviewClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (hop >= MaxRedirects || response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next))
                {
                    throw new PreviewRejectedException("This page redirected too many times or provided an invalid redirect.");
                }

                if (uri.Scheme == "https" && next.Scheme != "https")
                {
                    throw new PreviewRejectedException("The preview refused an insecure redirect.");
                }

                if (origin is not null && (next.GetLeftPart(UriPartial.Authority) != origin || next.Query.Length != 0))
                {
                    throw new PreviewRejectedException("A site favicon redirected to another origin or a URL with a query.");
                }

                uri = new Uri(next.GetLeftPart(UriPartial.Query));
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PreviewRejectedException("This page does not allow an anonymous preview.");
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (isHtml ?
                    mediaType is not ("text/html" or "application/xhtml+xml") :
                    mediaType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/x-icon" or "image/vnd.microsoft.icon" or "application/octet-stream"))
            {
                throw new PreviewRejectedException("The server did not return a supported preview format.");
            }

            return (uri, await readBody(uri, response.Content, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<byte[]> ReadImageAsync(HttpContent content, int byteLimit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > byteLimit)
        {
            throw new PreviewRejectedException("The preview response exceeded its size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input
                .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, byteLimit - (int)output.Length + 1)), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToArray();
            }

            if (output.Length + count > byteLimit)
            {
                throw new PreviewRejectedException("The preview response exceeded its size limit.");
            }

            output.Write(buffer, 0, count);
        }
    }

    private static byte[]? DecodeRaster(byte[] bytes, bool isIcon)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(input);
        if (codec?.EncodedFormat is not
            (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp or
            SKEncodedImageFormat.Gif or SKEncodedImageFormat.Ico))
        {
            return null;
        }

        var info = codec.Info;
        var maxSide = isIcon ? 512 : 4096;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > maxSide || info.Height > maxSide ||
            (long)info.Width * info.Height > 8_000_000)
        {
            return null;
        }

        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height));
        if (bitmap is null)
        {
            return null;
        }

        // Cache normalized, small, single-frame PNGs rather than feeding untrusted data to the UI decoder.
        var scale = Math.Min(1d, Math.Min((isIcon ? 32d : 640d) / info.Width, (isIcon ? 32d : 360d) / info.Height));
        var size = new SKImageInfo(Math.Max(1, (int)(info.Width * scale)), Math.Max(1, (int)(info.Height * scale)));
        using var resized = bitmap.Resize(size, new SKSamplingOptions(SKFilterMode.Linear));
        using var data = resized?.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    private WebPreview Unavailable(string reason)
    {
        LogWebPreviewUnavailableReason(reason);
        return new WebPreview(null, null, null, null, true);
    }

    private sealed record PreviewCacheEntry(WebPreview Preview, DateTimeOffset ExpiresAt);

    private sealed record SiteIcon(byte[]? Bytes, DateTimeOffset ExpiresAt);

    private sealed record PreviewStages(WebPreview Text, Task<byte[]?> Icon, Task<byte[]?> Cover);

    private sealed record PendingPreview(Task<PreviewStages?> Stages, Task<WebPreview> Complete);

    private sealed class PreviewRejectedException(string message) : IOException(message);

    [LoggerMessage(LogLevel.Debug, "Web preview processing failed: {ErrorType}")]
    partial void LogWebPreviewProcessingFailedErrorType(string errorType);

    [LoggerMessage(LogLevel.Debug, "Web preview unavailable: {Reason}")]
    partial void LogWebPreviewUnavailableReason(string reason);
}
