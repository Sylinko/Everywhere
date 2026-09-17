using System.Globalization;
using System.Text;
using Everywhere.Common;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;

namespace Everywhere.Web;

/// <summary>
/// Owns the installed browser version selected from PuppeteerSharp's cache.
/// Initialization is deliberately deferred until the built-in browser is first needed.
/// </summary>
public sealed class BuiltInBrowserCache
{
    public string CachePath { get; }

    private const string VersionFileName = "built-in-browser.version";
    private const int VersionFileFormat = 1;
    private const int MaxVersionFileSize = 4 * 1024;
    private const int MaxBuildIdLength = 128;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _versionFilePath;
    private readonly ILogger _logger;

    private InstalledBrowser? _browser;
    private bool _initialized;

    public BuiltInBrowserCache(string cachePath, ILoggerFactory loggerFactory)
    {
        CachePath = cachePath;
        _versionFilePath = Path.Combine(CachePath, VersionFileName);
        _logger = loggerFactory.CreateLogger<BuiltInBrowserCache>();
    }

    public BrowserFetcher CreateFetcher(BrowserFetcherOptions? options = null)
    {
        options ??= new BrowserFetcherOptions();
        options.Path = CachePath;
        options.Browser = SupportedBrowser.Chromium;
        return new BrowserFetcher(options)
        {
            CacheDir = CachePath,
            Browser = SupportedBrowser.Chromium,
        };
    }

    public async ValueTask<InstalledBrowser?> GetInstalledBrowserAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return _browser;

            Directory.CreateDirectory(CachePath);
            var fetcher = CreateFetcher();
            var installedBrowsers = fetcher.GetInstalledBrowsers()
                .AsValueEnumerable()
                .Where(browser =>
                    browser.Browser == SupportedBrowser.Chromium &&
                    browser.Platform == fetcher.Platform &&
                    File.Exists(browser.GetExecutablePath()))
                .ToArray();
            var pinnedVersion = await ReadVersionAsync(cancellationToken);

            _browser = installedBrowsers.AsValueEnumerable().FirstOrDefault(browser =>
                    pinnedVersion is { } version &&
                    version.Platform == browser.Platform &&
                    string.Equals(version.BuildId, browser.BuildId, StringComparison.Ordinal)) ??
                installedBrowsers.AsValueEnumerable().MaxBy(static browser => browser.BuildId, BuildIdComparer.Instance);
            _initialized = true;

            if (_browser is not null)
            {
                await WriteVersionAsync(_browser, cancellationToken);
            }

            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PinAsync(InstalledBrowser browser, CancellationToken cancellationToken)
    {
        var fetcher = CreateFetcher();
        if (browser.Browser != SupportedBrowser.Chromium || browser.Platform != fetcher.Platform)
        {
            throw new ArgumentException("The browser does not belong to the current built-in browser cache.", nameof(browser));
        }

        var executablePath = browser.GetExecutablePath();
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The downloaded built-in browser executable was not found.", executablePath);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteVersionAsync(browser, cancellationToken);
            _browser = browser;
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes other installed versions only after the selected browser has launched successfully.
    /// This keeps an existing installation available when a newly downloaded browser is incomplete or unusable.
    /// </summary>
    public void ConfirmLaunch(InstalledBrowser browser) => ScheduleCleanup(browser.BuildId);

    private void ScheduleCleanup(string retainedBuildId) =>
        Task.Run(() => CleanupAsync(retainedBuildId)).Detach(_logger.ToExceptionHandler());

    private async Task CleanupAsync(string retainedBuildId)
    {
        await _gate.WaitAsync();
        try
        {
            var fetcher = CreateFetcher();
            var obsoleteBrowsers = fetcher.GetInstalledBrowsers()
                .AsValueEnumerable()
                .Where(browser =>
                    browser.Browser == SupportedBrowser.Chromium &&
                    browser.Platform == fetcher.Platform &&
                    !string.Equals(browser.BuildId, retainedBuildId, StringComparison.Ordinal))
                .ToArray();

            foreach (var browser in obsoleteBrowsers)
            {
                try
                {
                    var executablePath = browser.GetExecutablePath();
                    if (!PathUtilities.IsInsideDirectory(executablePath, CachePath))
                    {
                        _logger.LogWarning(
                            "Skipping obsolete built-in browser version {BuildId} because its path escapes the managed cache.",
                            browser.BuildId);
                        continue;
                    }

                    _logger.LogInformation("Removing obsolete built-in browser version {BuildId}.", browser.BuildId);
                    fetcher.Uninstall(browser.BuildId);
                }
                catch (Exception ex)
                {
                    // A browser process or scanner may still hold files. A later initialization retries cleanup.
                    _logger.LogWarning(ex, "Failed to remove obsolete built-in browser version {BuildId}.", browser.BuildId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<PinnedBrowserVersion?> ReadVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_versionFilePath)) return null;

            await using var stream = new FileStream(
                _versionFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxVersionFileSize)
            {
                _logger.LogWarning("Ignoring oversized built-in browser version file at {Path}.", _versionFilePath);
                return null;
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (content.Length > MaxVersionFileSize)
            {
                _logger.LogWarning("Ignoring oversized built-in browser version file at {Path}.", _versionFilePath);
                return null;
            }

            var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
            if (lines.Length != 3 ||
                !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var format) ||
                format != VersionFileFormat ||
                !Enum.TryParse<Platform>(lines[1], ignoreCase: false, out var platform) ||
                !Enum.IsDefined(platform) ||
                !IsValidBuildId(lines[2]))
            {
                _logger.LogWarning("Ignoring invalid built-in browser version file at {Path}.", _versionFilePath);
                return null;
            }

            return new PinnedBrowserVersion(platform, lines[2]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            _logger.LogWarning(ex, "Failed to read built-in browser version file at {Path}.", _versionFilePath);
            return null;
        }
    }

    private async ValueTask WriteVersionAsync(InstalledBrowser browser, CancellationToken cancellationToken)
    {
        if (!IsValidBuildId(browser.BuildId))
        {
            throw new InvalidDataException("The built-in browser build ID is invalid.");
        }

        var tempPath = _versionFilePath + ".tmp";
        try
        {
            var content = string.Join(
                Environment.NewLine,
                VersionFileFormat.ToString(CultureInfo.InvariantCulture),
                browser.Platform.ToString(),
                browser.BuildId);
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, _versionFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Failed to remove temporary built-in browser version file at {Path}.", tempPath);
            }
        }
    }

    private static bool IsValidBuildId(string buildId) =>
        !string.IsNullOrWhiteSpace(buildId) &&
        buildId.Length <= MaxBuildIdLength &&
        buildId.All(static character => !char.IsControl(character));

    private readonly record struct PinnedBrowserVersion(Platform Platform, string BuildId);

    private sealed class BuildIdComparer : IComparer<string>
    {
        public static BuildIdComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xNumber) &&
                ulong.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yNumber))
            {
                return xNumber.CompareTo(yNumber);
            }

            return StringComparer.Ordinal.Compare(x, y);
        }
    }
}