using System.Globalization;
using System.Net;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Linux.Common;

/// <summary>
/// Provides the bundled script with the application's native HTTP proxy behavior.
/// </summary>
public static class LinuxUpdateDownload
{
    /// <summary>
    /// Downloads one bounded HTTPS response without initializing the desktop or logging proxy secrets.
    /// </summary>
    public static async Task<int> RunAsync(string address, string destination, string maximumBytes)
    {
        try
        {
            if (!long.TryParse(maximumBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0 || limit > 2L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid download limit.");
            }
            var proxyOverride = Environment.GetEnvironmentVariable("EVERYWHERE_UPDATE_PROXY");
            var settings = LinuxUpdateSettings.Load(Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json"),
                shouldReadChannel: false, shouldReadProxy: proxyOverride is null);
            using var handler = CreateHandler(settings.Proxy, proxyOverride);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Everywhere-Linux-Updater");
            var uri = new Uri(address);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            for (var redirects = 0; redirects <= 10; redirects++)
            {
                if (uri.Scheme != "https")
                {
                    throw new InvalidDataException("HTTPS is required.");
                }
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("Redirect has no location.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > limit)
                {
                    throw new InvalidDataException("Download exceeds its size limit.");
                }
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = File.Create(destination);
                var buffer = new byte[81920];
                var total = 0L;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token);
                    if (count == 0)
                    {
                        break;
                    }
                    total += count;
                    if (total > limit)
                    {
                        throw new InvalidDataException("Download exceeds its size limit.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                return 0;
            }
            throw new InvalidDataException("Too many redirects.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or HandledException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Download failed. Check the release source and Everywhere proxy settings.");
            return 1;
        }
    }

    /// <summary>
    /// Applies saved proxy behavior unless an explicit terminal override is supplied.
    /// </summary>
    public static HttpClientHandler CreateHandler(ProxySettings settings, string? proxyOverride)
    {
        var proxy = new DynamicWebProxy();
        if (proxyOverride is null)
        {
            proxy.ApplyProxySettings(settings);
        }
        if (proxyOverride is not null and not "system" and not "none")
        {
            var endpoint = proxyOverride.Contains("://", StringComparison.Ordinal) ? proxyOverride : $"http://{proxyOverride}";
            var uri = new Uri(endpoint);
            if (uri.Scheme is not ("http" or "https" or "socks5"))
            {
                throw new ArgumentException("Unsupported proxy scheme.");
            }
            return new HttpClientHandler { Proxy = new WebProxy(uri), UseProxy = true, AllowAutoRedirect = false };
        }
        return new HttpClientHandler { Proxy = proxy, UseProxy = proxyOverride != "none", AllowAutoRedirect = false };
    }
}