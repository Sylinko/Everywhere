using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using ZLinq;

namespace Everywhere.Web;

public sealed class WebBrowserHost : IWebBrowserHost
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
    private bool _isBrowserHeadless;
    private bool _isManualBrowser;
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
                        if (!that._isBrowserHeadless) return; // Do not auto-dispose headful browser
                        if (Volatile.Read(ref that._activeExtractions) > 0) return; // Do not dispose if there are active extractions

                        that._logger.LogDebug("Disposing browser after inactivity.");

                        if (that._browser is null) return;
                        var process = that._browserProcess;
                        await that._browser.CloseAsync();
                        DisposeHelper.DisposeToDefault(ref that._browser);

                        if (process is not null)
                        {
                            await that._watchdogManager.UnregisterProcessAsync(process.Id); // Kill if running
                        }

                        that._browserProcess = null;
                        that._isManualBrowser = false;
                        that._isBrowserHeadless = false;
                    }
                    finally
                    {
                        that._browserLock.Release();
                    }
                });
            },
            TimeSpan.FromMinutes(1)); // Dispose browser after 1 minutes of inactivity
    }

    /// <summary>
    /// Try to launch a browser with the given executable path and browser type. If the launch fails, return null instead of throwing an exception.
    /// </summary>
    /// <param name="headless"></param>
    /// <param name="args"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="HandledException"></exception>
    private async ValueTask<IBrowser> LaunchBrowserCoreAsync(bool headless, string[] args, CancellationToken cancellationToken)
    {
        var launchFailures = new List<string>();
        var userDataDir = RuntimeConstants.EnsureCacheFolderPath("plugins", "puppeteer", "userdata");

        // First try to launch previously launched browser
        var browser = await TryLaunchBrowserAsync("previous", _previousLaunchedBrowserPath, SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        // Then try to launch installed Edge browser
        browser = await TryLaunchBrowserAsync("edge", BrowserHelper.GetEdgePath(), SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        // Then try to launch installed Chrome browser
        browser = await TryLaunchBrowserAsync("chrome", BrowserHelper.GetChromePath(), SupportedBrowser.Chrome);
        if (browser is not null) return browser;

        // Finally download and launch Puppeteer browser
        var cachePath = RuntimeConstants.EnsureCacheFolderPath("plugins", "puppeteer");
        var browserFetcher = new BrowserFetcher(
            new BrowserFetcherOptions
            {
                CustomFileDownload = DownloadFileAsync
            })
        {
            CacheDir = cachePath,
            Browser = SupportedBrowser.Chromium,
        };
        var executablePath = browserFetcher.GetInstalledBrowsers().FirstOrDefault()?.GetExecutablePath();

        // Try to launch again in case the browser was downloaded previously
        browser = await TryLaunchBrowserAsync("cached-puppeteer", executablePath, SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        // We use two different URLs to download the browser for better reliability
        _logger.LogDebug("Downloading Puppeteer browser to cache directory: {CachePath}", cachePath);
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(10); // Set a reasonable timeout for the test connection
        browserFetcher.BaseUrl =
            await TestUrlConnectionAsync(httpClient, "https://storage.googleapis.com/chromium-browser-snapshots") ??
            await TestUrlConnectionAsync(httpClient, "https://cdn.npmmirror.com/binaries/chromium-browser-snapshots") ??
            throw new HandledException(
                new HttpRequestException(
                    "The embedded browser is not installed, and neither browser download URL was reachable. Check network access or install a supported browser."),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                showDetails: true);

        try
        {
            await browserFetcher.DownloadAsync(BrowserTag.Latest);
        }
        catch (Exception e)
        {
            throw new HandledException(
                new InvalidOperationException(
                    $"The embedded browser download failed: {e.Message}. Check network access and try again.",
                    e),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                showDetails: true);
        }

        // Try to launch again after download
        executablePath = browserFetcher.GetInstalledBrowsers().FirstOrDefault()?.GetExecutablePath();
        browser = await TryLaunchBrowserAsync("downloaded-puppeteer", executablePath, SupportedBrowser.Chromium);
        if (browser is not null) return browser;

        throw new HandledException(
            new InvalidOperationException(CreateBrowserLaunchFailureMessage(launchFailures)),
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_PuppeteerBrowserLaunchError_ErrorMessage),
            showDetails: true);

        async ValueTask<IBrowser?> TryLaunchBrowserAsync(string label, string? path, SupportedBrowser browserType)
        {
            if (path.IsNullOrEmpty())
            {
                launchFailures.Add($"{label}: browser path is empty.");
                return null;
            }

            if (!File.Exists(path))
            {
                launchFailures.Add($"{label}: browser path does not exist: {path}");
                return null;
            }

            try
            {
                _logger.LogDebug(
                    "Try launch Puppeteer browser executable at: {Path}. UserDataDir: {UserDataDir}, Headless: {Headless}",
                    path,
                    userDataDir,
                    headless);
                var launcher = new Launcher(_loggerFactory);
                var launchedBrowser = await launcher.LaunchAsync(
                    new LaunchOptions
                    {
                        ExecutablePath = path,
                        Browser = browserType,
                        Headless = headless,
                        UserDataDir = userDataDir,
                        DefaultViewport = null,
                        Args = args
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
                launchFailures.Add($"{label}: {path}: {e.GetType().Name}: {e.Message} UserDataDir: {userDataDir}");
                return null;
            }
        }

        static string CreateBrowserLaunchFailureMessage(IReadOnlyList<string> failures)
        {
            if (failures.Count == 0) return "All attempts to launch Puppeteer browser have failed.";

            var failureLines = new string[failures.Count];
            for (var i = 0; i < failures.Count; i++)
            {
                failureLines[i] = "- " + failures[i];
            }

            return "All attempts to launch Puppeteer browser have failed." +
                Environment.NewLine +
                string.Join(Environment.NewLine, failureLines);
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
        _logger.LogDebug("Opening shared browser window.");

        while (true)
        {
            await _browserLock.WaitAsync(cancellationToken);
            try
            {
                if (_browser is { IsClosed: false } &&
                    _isBrowserHeadless &&
                    Volatile.Read(ref _activeExtractions) > 0)
                {
                    _logger.LogDebug("Waiting for active headless extraction before opening browser window.");
                }
                else
                {
                    var browser = await EnsureManualBrowserAsync(cancellationToken);
                    var pages = await browser.PagesAsync();

                    var targetPage = pages.FirstOrDefault(p => string.IsNullOrEmpty(p.Url) || p.Url == "about:blank") ?? await browser.NewPageAsync();
                    await targetPage.BringToFrontAsync();
                    return;
                }
            }
            finally
            {
                _browserLock.Release();
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private async ValueTask<IBrowser> EnsureManualBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is { IsClosed: false } && !_isBrowserHeadless)
        {
            _isManualBrowser = true;
            return _browser;
        }

        if (_browser is { IsClosed: false })
        {
            await CloseBrowserAsync(killIfRunning: true);
        }

        _logger.LogDebug("Ensuring manual Puppeteer browser is initialized.");

        _browser = await LaunchBrowserCoreAsync(
            headless: false,
            args:
            [
                "--start-maximized",
                "--hide-crash-restore-bubble",
                "--disable-infobars"
            ],
            cancellationToken);

        _isBrowserHeadless = false;
        _isManualBrowser = true;
        await ResetHeadfulBrowserPagesAsync(_browser, cancellationToken);
        TrackBrowserDisconnect(_browser, _browser.Process);
        await RegisterTrackedProcessAsync(_browser.Process);
        _browserProcess = _browser.Process;
        return _browser;
    }

    private async ValueTask<IBrowser> EnsureExtractionBrowserAsync(CancellationToken cancellationToken)
    {
        var desiredHeadless = !_webBrowserSettings.ShowBrowser;
        if (_browser is { IsClosed: false })
        {
            if (_isBrowserHeadless == desiredHeadless)
            {
                return _browser;
            }

            if (!_isBrowserHeadless && (_isManualBrowser || !desiredHeadless))
            {
                return _browser;
            }

            if (Volatile.Read(ref _activeExtractions) > 0)
            {
                _logger.LogDebug("Reusing existing browser because extractions are active.");
                return _browser;
            }

            await CloseBrowserAsync(killIfRunning: true);
        }

        _logger.LogDebug("Ensuring extraction Puppeteer browser is initialized. Headless: {Headless}", desiredHeadless);

        var extraArgs = desiredHeadless ?
            new[]
            {
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--disable-setuid-sandbox",
                "--disable-extensions",
                "--disable-popup-blocking",
                "--blink-settings=imagesEnabled=false",
#if WINDOWS // This is a bug (regression?) on Windows:
            // https://github.com/puppeteer/puppeteer/issues/13121
            // https://issuetracker.google.com/issues/362545030
            // https://issues.chromium.org/issues/362706121
            // https://stackoverflow.com/questions/78996364/chrome-129-headless-shows-blank-window
                "--window-position=-2400,-2400"
#endif
            } :
            new[]
            {
                "--start-maximized",
                "--hide-crash-restore-bubble",
                "--disable-infobars"
            };

        _browser = await LaunchBrowserCoreAsync(desiredHeadless, extraArgs, cancellationToken);
        _isBrowserHeadless = desiredHeadless;
        _isManualBrowser = false;
        if (!desiredHeadless)
        {
            await ResetHeadfulBrowserPagesAsync(_browser, cancellationToken);
        }

        TrackBrowserDisconnect(_browser, _browser.Process);
        await RegisterTrackedProcessAsync(_browser.Process);
        _browserProcess = _browser.Process;
        return _browser;
    }

    private async Task ResetHeadfulBrowserPagesAsync(IBrowser browser, CancellationToken cancellationToken)
    {
        var pages = await browser.PagesAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var newPage = await browser.NewPageAsync();
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var page in pages)
        {
            try
            {
                await page.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close old page with URL: {Url}", page.Url);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        await newPage.BringToFrontAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RegisterTrackedProcessAsync(Process? process)
    {
        if (process is not null)
        {
            await _watchdogManager.RegisterProcessAsync(process.Id);
        }
    }

    private async Task CloseBrowserAsync(bool killIfRunning)
    {
        var browser = _browser;
        var process = _browserProcess;

        _browser = null;
        _browserProcess = null;
        _isManualBrowser = false;
        _isBrowserHeadless = false;

        if (browser is not null)
        {
            try
            {
                await browser.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to close browser cleanly.");
            }

            DisposeHelper.DisposeToDefault(ref browser);
        }

        if (process is not null)
        {
            await _watchdogManager.UnregisterProcessAsync(process.Id, killIfRunning);
        }
    }

    private void TrackBrowserDisconnect(IBrowser browser, Process? process)
    {
        browser.Disconnected += delegate
        {
            Task.Run(
                async () =>
                {
                    await _browserLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        _logger.LogDebug("Browser disconnected. Cleaning up state.");
                        if (_browser == browser)
                        {
                            _browser = null;
                            _isManualBrowser = false;
                        }

                        if (_browserProcess == process && process is not null)
                        {
                            await _watchdogManager.UnregisterProcessAsync(process.Id, killIfRunning: false);
                            if (_browserProcess == process)
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
    }

    public async Task<WebPageExtractionResult> ExtractPageAsync(string url, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Extracting web content from {Url}. Active extractions: {ActiveExtractions}", url, _activeExtractions);

        IBrowser browser;
        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            browser = await EnsureExtractionBrowserAsync(cancellationToken);
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
                var extractionState = await ConfigureExtractionPageAsync(page, cancellationToken);
                var responseContentType = await NavigateForExtractionAsync(page, url, cancellationToken);
                var documentContentType = await ReadDocumentContentTypeAsync(page);
                var contentType = responseContentType ?? extractionState.MainDocumentContentType ?? documentContentType;
                _logger.LogDebug(
                    "Web extraction content type for {Url}. Response: {ResponseContentType}, ObservedMainDocument: {ObservedContentType}, Document: {DocumentContentType}, Selected: {SelectedContentType}",
                    url,
                    responseContentType,
                    extractionState.MainDocumentContentType,
                    documentContentType,
                    contentType);

                if (IsMarkdownContentType(contentType))
                {
                    var markdown = await ReadBodyTextAsync(page);
                    if (!string.IsNullOrWhiteSpace(markdown))
                    {
                        var result = WebPageExtractionResult.Create(
                            WebExtractionUtilities.NormalizeMarkdown(markdown),
                            await page.GetTitleAsync(),
                            WebExtractionSource.MarkdownResponse);
                        return result with { Selection = WebExtractionSelection.Single(result) };
                    }
                }

                if (IsPlainTextContentType(contentType))
                {
                    var text = await ReadBodyTextAsync(page);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var result = WebPageExtractionResult.Create(
                            WebExtractionUtilities.NormalizeMarkdown(text),
                            await page.GetTitleAsync(),
                            WebExtractionSource.PlainTextResponse);
                        return result with { Selection = WebExtractionSelection.Single(result) };
                    }
                }

                var candidates = new List<WebPageExtractionResult>();
                var pageUri = new Uri(url);

                if (await TryExtractAccessibilityAsync(page, pageUri, cancellationToken) is { } accessibilityResult)
                {
                    candidates.Add(accessibilityResult);
                }

                if (await TryExtractReadabilityAsync(page, cancellationToken) is { } readabilityResult)
                {
                    candidates.Add(readabilityResult);
                }

                if (await TryExtractDomMainElementAsync(page, cancellationToken) is { } domMainResult)
                {
                    candidates.Add(domMainResult);
                }

                if (await TryExtractCleanedBodyAsync(page, cancellationToken) is { } cleanedBodyResult)
                {
                    candidates.Add(cleanedBodyResult);
                }

                LogExtractionCandidates(url, candidates);
                if (WebExtractionUtilities.SelectBestCandidate(candidates) is { } best)
                {
                    LogExtractionSelection(url, best.Selection);
                    _logger.LogDebug(
                        "Selected web extraction candidate for {Url}. Source: {Source}, Length: {Length}, Confidence: {Confidence}",
                        url,
                        best.Source,
                        best.ContentLength,
                        best.Selection?.Confidence);
                    return best;
                }

                return WebPageExtractionResult.Create(
                    "Failed to extract. The page may be too complex or not fully loaded.",
                    await page.GetTitleAsync(),
                    WebExtractionSource.CleanedBody);
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

    private void LogExtractionCandidates(string url, IReadOnlyList<WebPageExtractionResult> candidates)
    {
        foreach (var candidate in candidates)
        {
            _logger.LogDebug(
                "Web extraction candidate for {Url}. Source: {Source}, Length: {Length}",
                url,
                candidate.Source,
                candidate.ContentLength);
        }
    }

    private void LogExtractionSelection(string url, WebExtractionSelection? selection)
    {
        if (selection is null) return;

        foreach (var score in selection.Value.Scores)
        {
            _logger.LogDebug(
                "Web extraction score for {Url}. Source: {Source}, Score: {Score:0.000}, Confidence: {Confidence:0.000}, Length: {Length}, Structure: {Structure:0.000}, Consensus: {Consensus:0.000}, LinkNoise: {LinkNoise:0.000}, DuplicateLines: {DuplicateLines:0.000}, ShortLines: {ShortLines:0.000}",
                url,
                score.Source,
                score.Score,
                score.Confidence,
                score.ContentLength,
                score.Diagnostics.StructureScore,
                score.Diagnostics.ConsensusScore,
                score.Diagnostics.LinkNoiseRatio,
                score.Diagnostics.DuplicateLineRatio,
                score.Diagnostics.ShortLineRatio);
        }
    }

    private async Task<ExtractionPageState> ConfigureExtractionPageAsync(IPage page, CancellationToken cancellationToken)
    {
        var state = new ExtractionPageState();

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

        await page.SetRequestInterceptionAsync(true);
        cancellationToken.ThrowIfCancellationRequested();

        page.Request += async (_, e) =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested ||
                    e.Request.ResourceType is ResourceType.Image or ResourceType.Media or ResourceType.Font)
                {
                    await e.Request.AbortAsync();
                }
                else if (e.Request.ResourceType == ResourceType.Document)
                {
                    var headers = WebExtractionUtilities.BuildDocumentRequestHeaders(e.Request.Headers);
                    _logger.LogDebug(
                        "Continuing document request for {Url}. Accept: {Accept}",
                        e.Request.Url,
                        GetHeader(headers, "Accept"));
                    await e.Request.ContinueAsync(new Payload { Headers = headers });
                }
                else
                {
                    await e.Request.ContinueAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to handle intercepted request: {Url}", e.Request.Url);
            }
        };

        page.Response += (_, e) =>
        {
            if (e.Response.Request.ResourceType != ResourceType.Document) return;

            var contentType = GetContentType(e.Response);
            state.MainDocumentContentType = contentType;
            _logger.LogDebug(
                "Received document response for {Url}. Status: {Status}, Content-Type: {ContentType}",
                e.Response.Url,
                e.Response.Status,
                contentType);
        };

        await page.EvaluateFunctionOnNewDocumentAsync(WebExtractionScripts.BrowserHardening);
        cancellationToken.ThrowIfCancellationRequested();

        page.Dialog += async (_, e) =>
        {
            _logger.LogDebug("Auto-dismissing dialog: {Message}", e.Dialog.Message);
            await e.Dialog.Dismiss();
        };

        return state;
    }

    private async Task<string?> NavigateForExtractionAsync(IPage page, string url, CancellationToken cancellationToken)
    {
        page.DefaultNavigationTimeout = 30000;
        IResponse? response = null;
        try
        {
            response = await page.GoToAsync(
                url,
                new NavigationOptions
                {
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Networkidle2],
                    CancellationToken = cancellationToken
                });
            cancellationToken.ThrowIfCancellationRequested();

            await page.EvaluateFunctionAsync(WebExtractionScripts.AutoScroll);
            await Task.Delay(1000, cancellationToken);
        }
        catch (Exception ex) when (ex is WaitTaskTimeoutException or NavigationException { InnerException: TimeoutException })
        {
            _logger.LogWarning("Navigation timeout for {Url}, but proceeding with extraction anyway.", url);
        }

        return GetContentType(response);
    }

    private async Task<WebPageExtractionResult?> TryExtractAccessibilityAsync(IPage page, Uri pageUri, CancellationToken cancellationToken)
    {
        ICDPSession? session = null;
        try
        {
            session = await page.CreateCDPSessionAsync();
            var frameTreeResponse = await SendCdpJsonAsync(session, "Page.getFrameTree", parameters: null, cancellationToken);
            var frameIds = frameTreeResponse is { } frameTree ? WebCdpFrameTreeParser.ParseFrameIds(frameTree) : [];
            if (frameIds.Count == 0) frameIds = [string.Empty];

            var nodes = new List<WebAccessibilityNode>();
            foreach (var frameId in frameIds)
            {
                var parameters = string.IsNullOrEmpty(frameId) ? null : new { frameId };
                var axResponse = await SendCdpJsonAsync(session, "Accessibility.getFullAXTree", parameters, cancellationToken);
                if (axResponse is not { } axTree) continue;

                nodes.AddRange(WebAccessibilityNode.ParseNodes(axTree));
            }

            var markdown = WebAccessibilityMarkdownConverter.Convert(pageUri, nodes);
            return string.IsNullOrWhiteSpace(markdown) ?
                null :
                WebPageExtractionResult.Create(markdown, await page.GetTitleAsync(), WebExtractionSource.AccessibilityTree);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Accessibility extraction failed.");
            return null;
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DetachAsync(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to detach CDP session after accessibility extraction.");
                }
            }
        }
    }

    private async Task<WebPageExtractionResult?> TryExtractReadabilityAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.AddScriptTagAsync(new AddTagOptions { Content = WebExtractionScripts.ReadabilityJs });
            cancellationToken.ThrowIfCancellationRequested();

            var readabilityResult = await page.EvaluateFunctionAsync<ReadabilityResult>(WebExtractionScripts.ExtractWithReadability);
            cancellationToken.ThrowIfCancellationRequested();

            var markdown = !string.IsNullOrWhiteSpace(readabilityResult.Content) ?
                WebExtractionUtilities.ConvertHtmlToMarkdown(readabilityResult.Content) :
                WebExtractionUtilities.NormalizeMarkdown(readabilityResult.TextContent);

            return string.IsNullOrWhiteSpace(markdown) ?
                null :
                WebPageExtractionResult.Create(markdown, readabilityResult.Title ?? await page.GetTitleAsync(), WebExtractionSource.Readability);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Readability extraction failed.");
            return null;
        }
    }

    private async Task<WebPageExtractionResult?> TryExtractDomMainElementAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var result = await page.EvaluateFunctionAsync<DomExtractionResult?>(WebExtractionScripts.ExtractDomMainElement);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateDomResult(result, WebExtractionSource.DomMainElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DOM main element extraction failed.");
            return null;
        }
    }

    private async Task<WebPageExtractionResult?> TryExtractCleanedBodyAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var result = await page.EvaluateFunctionAsync<DomExtractionResult?>(WebExtractionScripts.ExtractCleanedBody);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateDomResult(result, WebExtractionSource.CleanedBody);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cleaned body extraction failed.");
            return null;
        }
    }

    private static WebPageExtractionResult? CreateDomResult(DomExtractionResult? result, WebExtractionSource source)
    {
        if (result is null) return null;

        var markdown = !string.IsNullOrWhiteSpace(result.Html) ?
            WebExtractionUtilities.ConvertHtmlToMarkdown(result.Html) :
            WebExtractionUtilities.NormalizeMarkdown(result.Text);
        return string.IsNullOrWhiteSpace(markdown) ? null : WebPageExtractionResult.Create(markdown, result.Title, source);
    }

    private static async Task<JsonElement?> SendCdpJsonAsync(
        ICDPSession session,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var commandTask = parameters is null ? session.SendAsync<JsonElement>(method) : session.SendAsync<JsonElement>(method, parameters);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var completedTask = await Task.WhenAny(commandTask, delayTask);
        if (completedTask != commandTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        return await commandTask;
    }

    private static string? GetContentType(IResponse? response)
    {
        return response?.Headers?.AsValueEnumerable()
            .Where(header => string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value)
            .FirstOrDefault();
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsMarkdownContentType(string? contentType) =>
        contentType?.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) == true ||
        contentType?.Contains("text/x-markdown", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPlainTextContentType(string? contentType) =>
        contentType?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) == true;

    private static Task<string> ReadBodyTextAsync(IPage page) =>
        page.EvaluateFunctionAsync<string>(WebExtractionScripts.ReadBodyText);

    private static Task<string> ReadDocumentContentTypeAsync(IPage page) =>
        page.EvaluateFunctionAsync<string>(WebExtractionScripts.ReadDocumentContentType);

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

    [Serializable]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private sealed class DomExtractionResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("html")]
        public string? Html { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private sealed class ExtractionPageState
    {
        public string? MainDocumentContentType { get; set; }
    }
}