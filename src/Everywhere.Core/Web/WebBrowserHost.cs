using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using ReverseMarkdown;
using ZLinq;

namespace Everywhere.Web;

public sealed partial class WebBrowserHost : IWebBrowserHost
{
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly WebBrowserSettings _webBrowserSettings;
    private readonly IWatchdogManager _watchdogManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebBrowserHost> _logger;
    private readonly DebounceExecutor<WebBrowserHost, ThreadingTimerImpl> _browserDisposer;

    private string? _previousLaunchedBrowserPath;
    private IBrowser? _browser;
    private Process? _browserProcess;
    private bool _isHeadless;
    private int _activeExtractions;

    static WebBrowserHost()
    {
        // Suppress unobserved Puppeteer exceptions
        Entrance.UnobservedTaskExceptionFilter += (_, e) =>
        {
            if (!e.Observed && e.Exception.Segregate().AsValueEnumerable().Any(ex => ex is PuppeteerException)) e.SetObserved();
        };
    }

    public WebBrowserHost(
        Settings settings,
        IWatchdogManager watchdogManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _webBrowserSettings = settings.Plugin.WebBrowser;
        _watchdogManager = watchdogManager;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebBrowserHost>();

        _browserDisposer = new DebounceExecutor<WebBrowserHost, ThreadingTimerImpl>(
            () => this,
            static that =>
            {
                Task.Run(async () =>
                {
                    await that._browserLock.WaitAsync();
                    try
                    {
                        if (!that._isHeadless) return; // Do not auto-dispose headful browser
                        if (Volatile.Read(ref that._activeExtractions) > 0) return; // Do not dispose if there are active extractions

                        that._logger.LogDebug("Disposing browser after inactivity.");

                        if (that._browser is null) return;
                        await that._browser.CloseAsync();
                        DisposeHelper.DisposeToDefault(ref that._browser);

                        if (that._browserProcess is { HasExited: false })
                        {
                            await that._watchdogManager.UnregisterProcessAsync(that._browserProcess.Id); // Kill if running
                            that._browserProcess = null;
                        }
                    }
                    finally
                    {
                        that._browserLock.Release();
                    }
                });
            },
            TimeSpan.FromMinutes(5)); // Dispose browser after 5 minutes of inactivity
    }

    /// <summary>
    /// Try to launch a browser with the given executable path and browser type. If the launch fails, return null instead of throwing an exception.
    /// </summary>
    /// <param name="headless"></param>
    /// <param name="extraArgs"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="HandledException"></exception>
    private async ValueTask<IBrowser> LaunchBrowserCoreAsync(bool headless, string[]? extraArgs, CancellationToken cancellationToken)
    {
        // First try to launch previously launched browser
        var browser = await TryLaunchBrowserAsync(_previousLaunchedBrowserPath, SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        // Then try to launch installed Edge browser
        browser = await TryLaunchBrowserAsync(BrowserHelper.GetEdgePath(), SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        // Then try to launch installed Chrome browser
        browser = await TryLaunchBrowserAsync(BrowserHelper.GetChromePath(), SupportedBrowser.Chrome);
        if (browser is not null) return browser;

        // Finally download and launch Puppeteer browser
        var cachePath = RuntimeConstants.EnsureCacheFolderPath("plugins", "puppeteer");
        var browserFetcher = new BrowserFetcher(new BrowserFetcherOptions
        {
            CustomFileDownload = DownloadFileAsync
        })
        {
            CacheDir = cachePath,
            Browser = SupportedBrowser.Chromium,
        };
        var executablePath = browserFetcher.GetInstalledBrowsers().FirstOrDefault()?.GetExecutablePath();

        // Try to launch again in case the browser was downloaded previously
        browser = await TryLaunchBrowserAsync(executablePath, SupportedBrowser.Chrome);
        if (browser is not null) return browser;

        // We use two different URLs to download the browser for better reliability
        _logger.LogDebug("Downloading Puppeteer browser to cache directory: {CachePath}", cachePath);
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10); // Set a reasonable timeout for the test connection
        browserFetcher.BaseUrl =
            await TestUrlConnectionAsync(httpClient, "https://storage.googleapis.com/chromium-browser-snapshots") ??
            await TestUrlConnectionAsync(httpClient, "https://cdn.npmmirror.com/binaries/chromium-browser-snapshots") ??
            throw new HandledException(
                new HttpRequestException("Failed to connect to the Puppeteer browser download URL."),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                showDetails: true);

        try
        {
            await browserFetcher.DownloadAsync(BrowserTag.Latest);
        }
        catch (Exception e)
        {
            throw new HandledException(
                new InvalidOperationException("Failed to download Puppeteer browser.", e),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                showDetails: true);
        }

        // Try to launch again after download
        browser = await TryLaunchBrowserAsync(executablePath, SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        throw new HandledException(
            new InvalidOperationException("All attempts to launch Puppeteer browser have failed."),
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_PuppeteerBrowserLaunchError_ErrorMessage),
            showDetails: true);

        async ValueTask<IBrowser?> TryLaunchBrowserAsync(string? path, SupportedBrowser browserType)
        {
            if (path.IsNullOrEmpty()) return null;
            if (!File.Exists(path)) return null;

            try
            {
                _logger.LogDebug("Try launch Puppeteer browser executable at: {Path}", path);
                var userDataDir = RuntimeConstants.EnsureCacheFolderPath("plugins", "puppeteer", "userdata");
                var launcher = new Launcher(_loggerFactory);
                var launchedBrowser = await launcher.LaunchAsync(
                    new LaunchOptions
                    {
                        ExecutablePath = path,
                        Browser = browserType,
                        Headless = headless,
                        UserDataDir = userDataDir,
                        DefaultViewport = null,
                        Args = extraArgs ?? []
                    });
                if (cancellationToken.IsCancellationRequested)
                {
                    await launchedBrowser.DisposeAsync();
                    throw new OperationCanceledException(cancellationToken);
                }

                _previousLaunchedBrowserPath = path;
                return launchedBrowser;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to launch Puppeteer browser at: {Path}", path);
                return null;
            }
        }

        async ValueTask<string?> TestUrlConnectionAsync(HttpClient client, string testUrl)
        {
            try
            {
                using var response = await client.GetAsync(testUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return testUrl;
                }

                _logger.LogWarning("Failed to connect to URL: {Url}, Status Code: {StatusCode}", testUrl, response.StatusCode);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to URL: {Url}", testUrl);
                return null;
            }
        }

        async Task DownloadFileAsync(string address, string filename)
        {
            using var client = _httpClientFactory.CreateClient();
            await using var downloadStream = await client.GetStreamAsync(address, cancellationToken).ConfigureAwait(false);
            await using var fileStream = File.Create(filename);
            await downloadStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task OpenBrowserAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Opening independent browser window.");

        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            var browser = await EnsureBrowserAsync(isManual: true, cancellationToken);
            var pages = await browser.PagesAsync();

            var targetPage = pages.FirstOrDefault(p => string.IsNullOrEmpty(p.Url) || p.Url == "about:blank") ?? await browser.NewPageAsync();
            await targetPage.BringToFrontAsync();
        }
        finally
        {
            _browserLock.Release();
        }
    }

    /// <summary>
    /// Ensure <see cref="_browser"/> is initialized and return it. If the browser process has exited, it will be relaunched.
    /// </summary>
    /// <param name="isManual"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async ValueTask<IBrowser> EnsureBrowserAsync(bool isManual, CancellationToken cancellationToken)
    {
        if (_browser is { IsClosed: false })
        {
            if (isManual && _isHeadless)
            {
                throw new HandledException(
                    new InvalidOperationException("The web browser is currently running in the background and cannot be opened."),
                    new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_CannotOpenBrowserInHeadlessMode_ErrorMessage),
                    showDetails: true);
            }

            return _browser;
        }

        _logger.LogDebug("Ensuring Puppeteer browser is initialized.");

        if (_browserProcess is { HasExited: false })
        {
            // Kill existing browser process if any
            var processId = _browserProcess.Id;
            _browserProcess = null;
            await _watchdogManager.UnregisterProcessAsync(processId); // Kill if running
        }

        _isHeadless = !isManual && !_webBrowserSettings.ShowBrowser;
        var extraArgs = _isHeadless ?
            new[]
            {
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--disable-setuid-sandbox",
                "--disable-extensions",
                "--disable-popup-blocking",
                "--blink-settings=imagesEnabled=false"
            } :
            new[]
            {
                "--start-maximized",
                "--hide-crash-restore-bubble",
                "--disable-infobars"
            };

        _browser = await LaunchBrowserCoreAsync(_isHeadless, extraArgs, cancellationToken);

        if (!_isHeadless)
        {
            // Prevent restoring previous pages on fresh launch
            var pages = await _browser.PagesAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var newPage = await _browser.NewPageAsync();
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var page in pages)
            {
                try { await page.CloseAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to close old page with URL: {Url}", page.Url);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            await newPage.BringToFrontAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var capturedBrowser = _browser;
        var capturedProcess = _browser.Process;
        _browser.Disconnected += delegate
        {
            Task.Run(
                async () =>
                {
                    await _browserLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        _logger.LogDebug("Browser disconnected. Cleaning up state.");
                        if (_browser == capturedBrowser)
                        {
                            _browser = null;
                        }

                        if (_browserProcess == capturedProcess && capturedProcess is not null)
                        {
                            await _watchdogManager.UnregisterProcessAsync(capturedProcess.Id, killIfRunning: false);
                            if (_browserProcess == capturedProcess)
                            {
                                _browserProcess = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handling browser disconnect.");
                    }
                    finally
                    {
                        _browserLock.Release();
                    }
                },
                CancellationToken.None).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        };

        _browserProcess = _browser.Process;
        if (_browserProcess is not null)
        {
            await _watchdogManager.RegisterProcessAsync(_browserProcess.Id);
        }

        return _browser;
    }

    /// <inheritdoc/>
    public async Task<string> ExtractAsync(string url, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Taking web snapshot...");

        IBrowser browser;
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            browser = await EnsureBrowserAsync(isManual: false, cancellationToken);
            Interlocked.Increment(ref _activeExtractions);
            _browserDisposer.Cancel();
        }
        finally
        {
            _browserLock.Release();
        }

        try
        {
            await using var page = await browser.NewPageAsync();
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await page.SetViewportAsync(
                    new ViewPortOptions
                    {
                        Width = 1920,
                        Height = 1080
                    });
                cancellationToken.ThrowIfCancellationRequested();

                await page.SetUserAgentAsync(
#if IsWindows
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#elif IsOSX
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#else
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#endif
                );
                cancellationToken.ThrowIfCancellationRequested();

                // Block requests for images, media, fonts, and stylesheets to speed up loading and reduce bandwidth
                await page.SetRequestInterceptionAsync(true);
                cancellationToken.ThrowIfCancellationRequested();

                page.Request += async (_, e) =>
                {
                    if (cancellationToken.IsCancellationRequested ||
                        e.Request.ResourceType is ResourceType.Image or ResourceType.Media or ResourceType.Font)
                    {
                        await e.Request.AbortAsync();
                    }
                    else
                    {
                        await e.Request.ContinueAsync();
                    }
                };

                await page.EvaluateFunctionOnNewDocumentAsync(
                    """
                    () => {
                        window.console.log = () => {};
                        window.console.info = () => {};
                        window.console.warn = () => {};
                        window.console.error = () => {};
                        window.console.debug = () => {};
                        window.console.dir = () => {};
                        window.console.dirxml = () => {};
                        window.console.trace = () => {};
                        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                        Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3] });
                    }
                    """);
                cancellationToken.ThrowIfCancellationRequested();

                page.Dialog += async (_, e) =>
                {
                    _logger.LogDebug("Auto-dismissing dialog: {Message}", e.Dialog.Message);
                    await e.Dialog.Dismiss();
                };

                page.DefaultNavigationTimeout = 30000;
                try
                {
                    await page.GoToAsync(
                        url,
                        new NavigationOptions
                        {
                            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Networkidle2],
                            CancellationToken = cancellationToken
                        });
                    cancellationToken.ThrowIfCancellationRequested();

                    // Auto-scroll to trigger lazy loading for SPAs
                    await page.EvaluateFunctionAsync(
                        """
                        async () => {
                            let lastHeight = document.body.scrollHeight;
                            let iterations = 0;
                            while (iterations < 3) {
                                window.scrollBy(0, 1000);
                                await new Promise(r => setTimeout(r, 800));
                                
                                const newHeight = document.body.scrollHeight;
                                if (newHeight === lastHeight) {
                                    break;
                                }
                                lastHeight = newHeight;
                                iterations++;
                            }
                        }
                        """);

                    // Give DOM a little more time to render after the last scroll
                    await Task.Delay(1000, cancellationToken);
                }
                catch (Exception ex) when (ex is WaitTaskTimeoutException or NavigationException { InnerException: TimeoutException })
                {
                    _logger.LogWarning("Navigation timeout for {Url}, but proceeding with extraction anyway.", url);
                }

                await page.AddScriptTagAsync(new AddTagOptions { Content = ReadabilityJs });
                cancellationToken.ThrowIfCancellationRequested();

                var readabilityResult = await page.EvaluateFunctionAsync<ReadabilityResult>(
                    """
                    () => {
                        var documentClone = document.cloneNode(true);
                        documentClone.querySelectorAll('svg').forEach(el => el.remove());
                        documentClone.querySelectorAll('img').forEach(el => {
                            if (el.src && el.src.startsWith('data:image/')) {
                                el.remove();
                            }
                        });
                        documentClone.querySelectorAll('a').forEach(el => {
                            if (el.href && el.href.length > 300) {
                                el.href = el.href.substring(0, 300) + '...';
                            }
                        });
                        var reader = new Readability(documentClone, {
                            keepClasses: true, 
                            charThreshold: 100,
                            classesToPreserve: ['markdown-body', 'highlight', 'code', 'table', 'comment', 'reply']
                        });
                        return reader.parse();
                    }
                    """);
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(readabilityResult.Content))
                {
                    return "Failed to extract. The page may be too complex or not fully loaded.";
                }

                var config = new Config
                {
                    UnknownTags = Config.UnknownTagsOption.Drop,
                    GithubFlavored = true,
                    RemoveComments = true,
                    SmartHrefHandling = true
                };
                var converter = new Converter(config);
                var markdownContent = converter.Convert(readabilityResult.Content);
                cancellationToken.ThrowIfCancellationRequested();

                return markdownContent;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeExtractions) == 0)
            {
                _browserDisposer.Trigger();
            }
        }
    }

    private static class BrowserHelper
    {
        public static string? GetChromePath()
        {
            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => SearchWindowsApplicationPath(Path.Combine("Google", "Chrome", "Application", "chrome.exe")),
                PlatformID.MacOSX => SearchMacOSApplicationPath("Google Chrome"),
                PlatformID.Unix => SearchLinuxApplicationPath("google-chrome"),
                _ => null
            };
        }

        public static string? GetEdgePath()
        {
            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => SearchWindowsApplicationPath(Path.Combine("Microsoft", "Edge", "Application", "msedge.exe")),
                PlatformID.MacOSX => SearchMacOSApplicationPath("Microsoft Edge"),
                PlatformID.Unix => SearchLinuxApplicationPath("microsoft-edge-stable"),
                _ => null
            };
        }

        private static string? SearchWindowsApplicationPath(string relativePath)
        {
            Span<Environment.SpecialFolder> rootPaths =
            [
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86
            ];
            return rootPaths
                .AsValueEnumerable()
                .Select(rootPath => Path.Combine(
                    Environment.GetFolderPath(rootPath),
                    relativePath))
                .FirstOrDefault(File.Exists);
        }

        private static string? SearchMacOSApplicationPath(string appName)
        {
            var path = $"/Applications/{appName}.app/Contents/MacOS/{appName}";
            return File.Exists(path) ? path : null;
        }

        private static string? SearchLinuxApplicationPath(string executableName)
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [];
            return paths
                .AsValueEnumerable()
                .Select(path => Path.Combine(path, executableName))
                .FirstOrDefault(File.Exists);
        }
    }

    [Serializable]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private readonly record struct ReadabilityResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("textContent")] string? TextContent,
        [property: JsonPropertyName("siteName")] string? SiteName
    );
}