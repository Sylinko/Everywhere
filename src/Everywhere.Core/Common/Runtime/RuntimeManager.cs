using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Everywhere.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZLinq;

namespace Everywhere.Common;

public sealed class RuntimeManager(
    IFileDownloadService fileDownloadService,
    IHttpClientFactory httpClientFactory,
    ILogger<RuntimeManager> logger
) : IRuntimeManager, IAsyncInitializer
{
    private const string GitHubProxyPrefix = "https://gh-proxy.com/";
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly Lock _statusLock = new();
    private Dictionary<RuntimeKind, RuntimeStatus> _statuses = CreateEmptyStatuses();

    public event EventHandler? StatusChanged;

    public bool HasRefreshed { get; private set; }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        Task.Run(() => RefreshAsync()).Detach(logger.ToExceptionHandler());
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var statuses = CreateEmptyStatuses();
        foreach (var kind in Enum.GetValues<RuntimeKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses[kind] = DetectRuntime(kind);
        }

        lock (_statusLock)
        {
            _statuses = statuses;
            HasRefreshed = true;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    public IReadOnlyList<string> GetPathEntries()
    {
        return [EnsureShimsPath()];
    }

    public RuntimeDependency? GetMissingDependency(string command)
    {
        if (!HasRefreshed) return null;
        var dependency = RuntimeDependencyDetector.Detect(command);
        return dependency is not null && !IsAvailable(dependency.Kind) ? dependency : null;
    }

    public bool IsAvailable(RuntimeKind kind)
    {
        lock (_statusLock)
        {
            return _statuses.TryGetValue(kind, out var status) && status.IsAvailable;
        }
    }

    public async Task InstallAsync(
        RuntimeKind kind,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (kind == RuntimeKind.Docker) return;

        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (kind)
            {
                case RuntimeKind.Uv:
                    await InstallUvAsync(progress, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeKind.NodeJs:
                    await InstallNodeAsync(progress, cancellationToken).ConfigureAwait(false);
                    break;
                case RuntimeKind.Bun:
                    await InstallBunAsync(progress, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installLock.Release();
        }
    }

    public static string GetRuntimeDisplayName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Uv => "uv",
        RuntimeKind.NodeJs => "Node.js",
        RuntimeKind.Bun => "Bun",
        RuntimeKind.Docker => "Docker",
        _ => kind.ToString()
    };

    internal static string? SelectLatestLtsNodeVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("lts", out var lts) ||
                lts.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            {
                continue;
            }

            return item.GetProperty("version").GetString();
        }

        return null;
    }

    private static RuntimeStatus DetectRuntime(RuntimeKind kind)
    {
        var primaryCommand = GetPrimaryCommand(kind);
        var managedPath = FindManagedCommand(primaryCommand);
        if (managedPath is not null)
        {
            return new RuntimeStatus(kind, RuntimeSource.Managed, managedPath);
        }

        var systemPath = FindCommandOnPath(primaryCommand);
        return systemPath is null ?
            new RuntimeStatus(kind, RuntimeSource.None, null) :
            new RuntimeStatus(kind, RuntimeSource.System, systemPath);
    }

    private async Task InstallUvAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var assetName = GetUvAssetName();
        var asset = await GetGitHubLatestAssetAsync("astral-sh", "uv", assetName, cancellationToken).ConfigureAwait(false);
        var version = asset.Version ?? "latest";
        var archivePath = await DownloadRuntimeArchiveAsync(RuntimeKind.Uv, asset, progress, cancellationToken).ConfigureAwait(false);

        var installRoot = await ExtractAndPublishAsync(RuntimeKind.Uv, version, archivePath, cancellationToken).ConfigureAwait(false);
        var uv = FindRequiredFile(installRoot, GetExecutableFileName("uv"));
        var uvx = FindRequiredFile(installRoot, GetExecutableFileName("uvx"));
        CreateExecutableShim("uv", uv);
        CreateExecutableShim("uvx", uvx);
    }

    private async Task InstallBunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var assetName = GetBunAssetName();
        var asset = await GetGitHubLatestAssetAsync("oven-sh", "bun", assetName, cancellationToken).ConfigureAwait(false);
        var version = asset.Version ?? "latest";
        var archivePath = await DownloadRuntimeArchiveAsync(RuntimeKind.Bun, asset, progress, cancellationToken).ConfigureAwait(false);

        var installRoot = await ExtractAndPublishAsync(RuntimeKind.Bun, version, archivePath, cancellationToken).ConfigureAwait(false);
        var bun = FindRequiredFile(installRoot, GetExecutableFileName("bun"));
        CreateExecutableShim("bun", bun);
        CreateBunxShim(bun);
    }

    private async Task InstallNodeAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
        var indexJson = await TryGetStringAsync(httpClient, "https://nodejs.org/dist/index.json", cancellationToken).ConfigureAwait(false) ??
            await TryGetStringAsync(httpClient, "https://npmmirror.com/mirrors/node/index.json", cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Failed to load Node.js release index.");
        var version = SelectLatestLtsNodeVersion(indexJson) ??
            throw new InvalidOperationException("Failed to find the latest Node.js LTS version.");

        var assetName = GetNodeAssetName(version);
        var shasums = await TryGetStringAsync(httpClient, $"https://nodejs.org/dist/{version}/SHASUMS256.txt", cancellationToken)
                .ConfigureAwait(false) ??
            await TryGetStringAsync(httpClient, $"https://npmmirror.com/mirrors/node/{version}/SHASUMS256.txt", cancellationToken)
                .ConfigureAwait(false);
        var digest = TryGetSha256FromShasums(shasums, assetName);

        var archivePath = await fileDownloadService.DownloadAsync(
            new FileDownloadRequest(
                GetRuntimeArchivePath(RuntimeKind.NodeJs, assetName),
                [
                    new FileDownloadSource($"https://nodejs.org/dist/{version}/{assetName}", "Node.js"),
                    new FileDownloadSource($"https://npmmirror.com/mirrors/node/{version}/{assetName}", "npmmirror")
                ],
                Sha256Digest: digest),
            progress,
            cancellationToken).ConfigureAwait(false);

        var installRoot = await ExtractAndPublishAsync(RuntimeKind.NodeJs, version.TrimStart('v'), archivePath, cancellationToken)
            .ConfigureAwait(false);
        var node = FindRequiredFile(installRoot, GetExecutableFileName("node"));
        CreateExecutableShim("node", node);
        CreateNodeToolShim("npm", node, installRoot);
        CreateNodeToolShim("npx", node, installRoot);
    }

    private async Task<GitHubRuntimeAsset> GetGitHubLatestAssetAsync(
        string owner,
        string repository,
        string assetName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
            await using var stream = await httpClient.GetStreamAsync(
                $"https://api.github.com/repos/{owner}/{repository}/releases/latest",
                cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync(
                stream,
                DownloadJsonSerializerContext.Default.GitHubRelease,
                cancellationToken).ConfigureAwait(false);
            var asset = release?.Assets?.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset is not null)
            {
                return new GitHubRuntimeAsset(
                    release?.TagName?.TrimStart('v'),
                    asset.Name,
                    asset.Size,
                    asset.Digest,
                    [
                        new FileDownloadSource(asset.BrowserDownloadUrl, "GitHub"),
                        new FileDownloadSource(GitHubProxyPrefix + asset.BrowserDownloadUrl, "GitHub proxy")
                    ]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to query latest GitHub release for {Owner}/{Repository}.", owner, repository);
        }

        var latestUrl = $"https://github.com/{owner}/{repository}/releases/latest/download/{assetName}";
        return new GitHubRuntimeAsset(
            null,
            assetName,
            null,
            null,
            [
                new FileDownloadSource(latestUrl, "GitHub"),
                new FileDownloadSource(GitHubProxyPrefix + latestUrl, "GitHub proxy")
            ]);
    }

    private async Task<string> DownloadRuntimeArchiveAsync(
        RuntimeKind kind,
        GitHubRuntimeAsset asset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return await fileDownloadService.DownloadAsync(
            new FileDownloadRequest(
                GetRuntimeArchivePath(kind, asset.Name),
                asset.Sources,
                asset.Size,
                asset.Digest),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExtractAndPublishAsync(
        RuntimeKind kind,
        string version,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(RuntimeConstants.BinFolderPath, "installing", $"{kind}-{Guid.CreateVersion7():N}");
        var extractionPath = Path.Combine(stagingRoot, "extract");
        Directory.CreateDirectory(extractionPath);

        try
        {
            await ExtractArchiveAsync(archivePath, extractionPath, cancellationToken).ConfigureAwait(false);
            var extractedRoot = GetSingleRootOrSelf(extractionPath);
            var installRoot = Path.Combine(GetAppsPath(), kind.ToString().ToLowerInvariant(), version);

            SafeDeleteDirectory(installRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(installRoot)!);
            Directory.Move(extractedRoot, installRoot);

            return installRoot;
        }
        finally
        {
            SafeDeleteDirectory(stagingRoot);
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archivePath, destinationPath, overwriteFiles: true, cancellationToken: cancellationToken);
            await Task.CompletedTask;
            return;
        }

        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList = { "-xf", archivePath, "-C", destinationPath },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start tar for archive extraction.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar failed to extract runtime archive: {await errorTask.ConfigureAwait(false)}");
        }
    }

    private static void CreateExecutableShim(string commandName, string targetPath)
    {
        var shimsPath = EnsureShimsPath();
        if (OperatingSystem.IsWindows() && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (TryCreateWindowsExecutableShim(commandName, targetPath, null))
            {
                return;
            }

            File.Copy(targetPath, Path.Combine(shimsPath, commandName + ".exe"), overwrite: true);
            DeleteIfExists(Path.Combine(shimsPath, commandName + ".cmd"));
            DeleteIfExists(Path.Combine(shimsPath, commandName + ".shim"));
            return;
        }

        CreateScriptShim(commandName, targetPath);
    }

    private static void CreateNodeToolShim(string commandName, string nodePath, string installRoot)
    {
        var npmScript = OperatingSystem.IsWindows() ?
            Path.Combine(installRoot, "node_modules", "npm", "bin", $"{commandName}-cli.js") :
            Path.Combine(installRoot, "lib", "node_modules", "npm", "bin", $"{commandName}-cli.js");
        if (!File.Exists(npmScript))
        {
            throw new FileNotFoundException($"Node.js {commandName} script was not found.", npmScript);
        }

        if (OperatingSystem.IsWindows())
        {
            if (!TryCreateWindowsExecutableShim(commandName, nodePath, QuoteArgument(npmScript)))
            {
                CreateWindowsCmdShim(commandName, $"\"{nodePath}\" \"{npmScript}\" %*");
            }
        }
        else
        {
            CreateUnixScriptShim(commandName, $"exec \"{nodePath}\" \"{npmScript}\" \"$@\"");
        }
    }

    private static void CreateBunxShim(string bunPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!TryCreateWindowsExecutableShim("bunx", bunPath, "x"))
            {
                CreateWindowsCmdShim("bunx", $"\"{bunPath}\" x %*");
            }
        }
        else
        {
            CreateUnixScriptShim("bunx", $"exec \"{bunPath}\" x \"$@\"");
        }
    }

    private static void CreateScriptShim(string commandName, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateWindowsCmdShim(commandName, $"\"{targetPath}\" %*");
        }
        else
        {
            CreateUnixScriptShim(commandName, $"exec \"{targetPath}\" \"$@\"");
        }
    }

    private static void CreateWindowsCmdShim(string commandName, string commandLine)
    {
        var path = Path.Combine(EnsureShimsPath(), commandName + ".cmd");
        File.WriteAllText(path, $"@echo off\r\n{commandLine}\r\n", Encoding.ASCII);
        DeleteIfExists(Path.Combine(EnsureShimsPath(), commandName + ".exe"));
        DeleteIfExists(Path.Combine(EnsureShimsPath(), commandName + ".shim"));
    }

    private static void CreateUnixScriptShim(string commandName, string commandLine)
    {
        var path = Path.Combine(EnsureShimsPath(), commandName);
        File.WriteAllText(path, $"#!/usr/bin/env sh\n{commandLine}\n", Encoding.ASCII);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static bool TryCreateWindowsExecutableShim(string commandName, string targetPath, string? args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var bundledShimPath = GetBundledWindowsShimPath();
        if (bundledShimPath is null) return false;

        var shimsPath = EnsureShimsPath();
        File.Copy(bundledShimPath, Path.Combine(shimsPath, commandName + ".exe"), overwrite: true);

        var shimFileBuilder = new StringBuilder()
            .Append("path = ")
            .AppendLine(targetPath);
        if (!string.IsNullOrWhiteSpace(args))
        {
            shimFileBuilder
                .Append("args = ")
                .AppendLine(args);
        }

        File.WriteAllText(Path.Combine(shimsPath, commandName + ".shim"), shimFileBuilder.ToString(), new UTF8Encoding(false));
        DeleteIfExists(Path.Combine(shimsPath, commandName + ".cmd"));
        return true;
    }

    private static string? GetBundledWindowsShimPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Shims", "kiennq", "shim.exe");
        return File.Exists(path) ? path : null;
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ') || argument.Contains('\t') ? $"\"{argument}\"" : argument;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string? FindManagedCommand(string command)
    {
        return FindCommandInPath(command, EnsureShimsPath());
    }

    private static string? FindCommandOnPath(string command)
    {
        var path = EnvironmentVariableUtilities.GetLatestPathVariable();
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var result = FindCommandInPath(command, directory);
            if (result is not null) return result;
        }

        return null;
    }

    private static string? FindCommandInPath(string command, string directory)
    {
        if (!Directory.Exists(directory)) return null;

        foreach (var candidate in GetExecutableCandidates(command))
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static IEnumerable<string> GetExecutableCandidates(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return command + ".exe";
            yield return command + ".cmd";
            yield return command + ".bat";
        }

        yield return command;
    }

    private static string GetPrimaryCommand(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Uv => "uvx",
        RuntimeKind.NodeJs => "npx",
        RuntimeKind.Bun => "bunx",
        RuntimeKind.Docker => "docker",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string GetExecutableFileName(string command) => OperatingSystem.IsWindows() ? command + ".exe" : command;

    private static string GetRuntimeArchivePath(RuntimeKind kind, string assetName)
    {
        var downloadPath = RuntimeConstants.EnsureCacheFolderPath("runtime-downloads", kind.ToString().ToLowerInvariant());
        return Path.Combine(downloadPath, assetName);
    }

    private static string EnsureShimsPath() => Directory.CreateDirectory(Path.Combine(RuntimeConstants.BinFolderPath, "shims")).FullName;

    private static string GetAppsPath() => Directory.CreateDirectory(Path.Combine(RuntimeConstants.BinFolderPath, "apps")).FullName;

    private static Dictionary<RuntimeKind, RuntimeStatus> CreateEmptyStatuses() =>
        Enum.GetValues<RuntimeKind>().ToDictionary(k => k, k => new RuntimeStatus(k, RuntimeSource.None, null));

    private static string GetUvAssetName()
    {
        return (RuntimeInformation.ProcessArchitecture, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()) switch
        {
            (Architecture.Arm64, true, _, _) => "uv-aarch64-pc-windows-msvc.zip",
            (_, true, _, _) => "uv-x86_64-pc-windows-msvc.zip",
            (Architecture.Arm64, _, true, _) => "uv-aarch64-unknown-linux-gnu.tar.gz",
            (_, _, true, _) => "uv-x86_64-unknown-linux-gnu.tar.gz",
            (Architecture.Arm64, _, _, true) => "uv-aarch64-apple-darwin.tar.gz",
            (_, _, _, true) => "uv-x86_64-apple-darwin.tar.gz",
            _ => throw new PlatformNotSupportedException("Unsupported platform for uv.")
        };
    }

    private static string GetBunAssetName()
    {
        return (RuntimeInformation.ProcessArchitecture, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()) switch
        {
            (Architecture.Arm64, true, _, _) => "bun-windows-aarch64.zip",
            (_, true, _, _) => "bun-windows-x64.zip",
            (Architecture.Arm64, _, true, _) => "bun-linux-aarch64.zip",
            (_, _, true, _) => "bun-linux-x64.zip",
            (Architecture.Arm64, _, _, true) => "bun-darwin-aarch64.zip",
            (_, _, _, true) => "bun-darwin-x64.zip",
            _ => throw new PlatformNotSupportedException("Unsupported platform for Bun.")
        };
    }

    private static string GetNodeAssetName(string version)
    {
        return (RuntimeInformation.ProcessArchitecture, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()) switch
        {
            (Architecture.Arm64, true, _, _) => $"node-{version}-win-arm64.zip",
            (_, true, _, _) => $"node-{version}-win-x64.zip",
            (Architecture.Arm64, _, true, _) => $"node-{version}-linux-arm64.tar.xz",
            (_, _, true, _) => $"node-{version}-linux-x64.tar.xz",
            (Architecture.Arm64, _, _, true) => $"node-{version}-darwin-arm64.tar.gz",
            (_, _, _, true) => $"node-{version}-darwin-x64.tar.gz",
            _ => throw new PlatformNotSupportedException("Unsupported platform for Node.js.")
        };
    }

    private static string? TryGetSha256FromShasums(string? shasums, string assetName)
    {
        if (string.IsNullOrWhiteSpace(shasums)) return null;

        return shasums.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .AsValueEnumerable()
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 2 && string.Equals(parts[^1], assetName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0]).FirstOrDefault();
    }

    private static async Task<string?> TryGetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSingleRootOrSelf(string extractionPath)
    {
        var entries = Directory.EnumerateFileSystemEntries(extractionPath).ToList();
        return entries.Count == 1 && Directory.Exists(entries[0]) ? entries[0] : extractionPath;
    }

    private static string FindRequiredFile(string root, string fileName)
    {
        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault() ??
            throw new FileNotFoundException($"Runtime executable was not found after extraction: {fileName}");
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(
                RuntimeConstants.BinFolderPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete directory outside runtime bin folder: {fullPath}");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private sealed record GitHubRuntimeAsset(
        string? Version,
        string Name,
        long? Size,
        string? Digest,
        IReadOnlyList<FileDownloadSource> Sources
    );
}