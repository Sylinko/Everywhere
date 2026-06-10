using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Media.SpeechRecognition.Sherpa;

public sealed partial class SherpaOnnxModelInstaller(
    SherpaOnnxModelRegistry registry,
    IFileDownloadService fileDownloadService,
    ILogger<SherpaOnnxModelInstaller> logger) : ObservableObject
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _installGate = new(1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial SherpaOnnxModelInstallState State { get; private set; } = SherpaOnnxModelInstallState.NotInstalled;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? CurrentModelId { get; private set; }

    [ObservableProperty]
    public partial Exception? LastException { get; private set; }

    public bool IsBusy => State is
        SherpaOnnxModelInstallState.Downloading or
        SherpaOnnxModelInstallState.Verifying or
        SherpaOnnxModelInstallState.Installing;

    public bool IsInstalled => State == SherpaOnnxModelInstallState.Installed;

    public bool CanInstall => !IsBusy && State != SherpaOnnxModelInstallState.Installed;

    public string GetInstalledModelPath(SherpaOnnxModelMetadata metadata) =>
        Path.Combine(GetInstalledRoot(), metadata.Id);

    public async Task<SherpaOnnxModelInstallState> RefreshStateAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = registry.GetModel(modelId);
        CurrentModelId = metadata.Id;
        LastException = null;
        Progress = 0d;

        try
        {
            var installedPath = GetInstalledModelPath(metadata);
            State = SherpaOnnxModelInstallState.Verifying;
            State = await IsInstalledModelValidAsync(metadata, installedPath, cancellationToken).ConfigureAwait(false) ?
                SherpaOnnxModelInstallState.Installed :
                Directory.Exists(installedPath) ? SherpaOnnxModelInstallState.Corrupted : SherpaOnnxModelInstallState.NotInstalled;
            return State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastException = ex;
            State = SherpaOnnxModelInstallState.Corrupted;
            logger.LogWarning(ex, "Failed to refresh sherpa-onnx model install state for {ModelId}.", metadata.Id);
            return State;
        }
    }

    public async Task<bool> IsInstalledAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        var metadata = registry.GetModel(modelId);
        return await IsInstalledModelValidAsync(metadata, GetInstalledModelPath(metadata), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EnsureInstalledAsync(
        string? modelId = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = registry.GetModel(modelId);
        CurrentModelId = metadata.Id;
        LastException = null;
        Progress = 0d;

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installedPath = GetInstalledModelPath(metadata);
            State = SherpaOnnxModelInstallState.Verifying;
            if (await IsInstalledModelValidAsync(metadata, installedPath, cancellationToken).ConfigureAwait(false))
            {
                State = SherpaOnnxModelInstallState.Installed;
                return installedPath;
            }

            State = Directory.Exists(installedPath) ? SherpaOnnxModelInstallState.Corrupted : SherpaOnnxModelInstallState.NotInstalled;
            var archivePath = await DownloadArchiveAsync(metadata, progress, cancellationToken).ConfigureAwait(false);
            return await InstallArchiveAsync(metadata, archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            LastException = ex;
            Progress = 0d;
            State = Directory.Exists(GetInstalledModelPath(metadata)) ?
                SherpaOnnxModelInstallState.Corrupted :
                SherpaOnnxModelInstallState.NotInstalled;
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastException = ex;
            if (State == SherpaOnnxModelInstallState.Downloading)
            {
                State = SherpaOnnxModelInstallState.DownloadFailed;
            }

            logger.LogError(ex, "Failed to install sherpa-onnx model {ModelId}.", metadata.Id);
            throw;
        }
        finally
        {
            _installGate.Release();
        }
    }

    public async static ValueTask<bool> IsInstalledModelValidAsync(
        SherpaOnnxModelMetadata metadata,
        string installedPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(installedPath)) return false;

        var manifestPath = GetManifestPath(installedPath);
        if (!File.Exists(manifestPath)) return false;

        InstalledModelManifest? manifest;
        await using (var stream = File.OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<InstalledModelManifest>(
                stream,
                JsonSerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        if (manifest is null ||
            !string.Equals(manifest.ModelId, metadata.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.ArchiveSha256, NormalizeSha256(metadata.ArchiveSha256), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return GetRequiredFiles(metadata).All(relativePath => File.Exists(Path.Combine(installedPath, relativePath)));
    }

    private async Task<string> DownloadArchiveAsync(
        SherpaOnnxModelMetadata metadata,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        State = SherpaOnnxModelInstallState.Downloading;

        var archivePath = Path.Combine(GetArchiveRoot(), metadata.ArchiveFileName);
        var sources = metadata.Mirrors
            .AsValueEnumerable()
            .Select(mirror => new FileDownloadSource(mirror.Url, mirror.SourceId))
            .ToList();

        var mergedProgress = new Progress<double>(p =>
        {
            Progress = p;
            progress?.Report(p);
        });

        return await fileDownloadService.DownloadAsync(
            new FileDownloadRequest(
                archivePath,
                sources,
                metadata.ArchiveSize,
                metadata.ArchiveSha256),
            mergedProgress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> InstallArchiveAsync(SherpaOnnxModelMetadata metadata, string archivePath, CancellationToken cancellationToken)
    {
        State = SherpaOnnxModelInstallState.Installing;

        var stagingRoot = Path.Combine(GetStagingRoot(), $"{metadata.Id}-{Guid.CreateVersion7():N}");
        var extractionPath = Path.Combine(stagingRoot, "extract");
        Directory.CreateDirectory(extractionPath);

        try
        {
            await VerifyArchiveHashAsync(archivePath, metadata.ArchiveSha256, cancellationToken).ConfigureAwait(false);
            await ExtractArchiveAsync(archivePath, extractionPath, cancellationToken).ConfigureAwait(false);

            var extractedRoot = GetSingleRootOrSelf(extractionPath);
            var modelRoot = FindModelRoot(metadata, extractedRoot);
            foreach (var requiredFile in GetRequiredFiles(metadata))
            {
                var path = Path.Combine(modelRoot, requiredFile);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"Required model file is missing: {requiredFile}", path);
                }
            }

            await WriteManifestAsync(metadata, modelRoot, cancellationToken).ConfigureAwait(false);

            var finalPath = GetInstalledModelPath(metadata);
            var oldPath = finalPath + ".old-" + Guid.CreateVersion7().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (Directory.Exists(finalPath))
            {
                Directory.Move(finalPath, oldPath);
            }

            Directory.Move(modelRoot, finalPath);
            SafeDeleteDirectory(oldPath);
            Progress = 1d;
            State = SherpaOnnxModelInstallState.Installed;
            return finalPath;
        }
        finally
        {
            SafeDeleteDirectory(stagingRoot);
        }
    }

    private static async Task VerifyArchiveHashAsync(string archivePath, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, NormalizeSha256(expectedSha256), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(archivePath);
            throw new InvalidOperationException($"Downloaded model archive hash mismatch: {Path.GetFileName(archivePath)}.");
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archivePath, destinationPath, true, cancellationToken).ConfigureAwait(false);
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
            throw new InvalidOperationException("Failed to start tar to extract sherpa-onnx model archive.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar failed to extract sherpa-onnx model archive: {await errorTask.ConfigureAwait(false)}");
        }
    }

    private static string FindModelRoot(SherpaOnnxModelMetadata metadata, string root)
    {
        if (GetRequiredFiles(metadata).All(file => File.Exists(Path.Combine(root, file))))
        {
            return root;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (GetRequiredFiles(metadata).All(file => File.Exists(Path.Combine(directory, file))))
            {
                return directory;
            }
        }

        return root;
    }

    private static string GetSingleRootOrSelf(string extractionPath)
    {
        var directories = Directory.GetDirectories(extractionPath);
        var files = Directory.GetFiles(extractionPath);
        return directories.Length == 1 && files.Length == 0 ? directories[0] : extractionPath;
    }

    private static async Task WriteManifestAsync(SherpaOnnxModelMetadata metadata, string modelRoot, CancellationToken cancellationToken)
    {
        var manifest = new InstalledModelManifest(
            metadata.Id,
            NormalizeSha256(metadata.ArchiveSha256),
            DateTimeOffset.UtcNow,
            GetRequiredFiles(metadata));

        await using var stream = File.Create(GetManifestPath(modelRoot));
        await JsonSerializer.SerializeAsync(stream, manifest, JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> GetRequiredFiles(SherpaOnnxModelMetadata metadata)
    {
        List<string> files = metadata.RequiredFiles switch
        {
            SherpaOnnxRequiredFiles.Transducer transducerFiles =>
            [
                transducerFiles.Encoder,
                transducerFiles.Decoder,
                transducerFiles.Joiner
            ],
            SherpaOnnxRequiredFiles.Zipformer2Ctc ctcFiles => [ctcFiles.Model],
            _ => throw new NotSupportedException($"Unsupported sherpa-onnx model file definition: {metadata.RequiredFiles.GetType().Name}.")
        };

        files.Add(metadata.RequiredFiles.Tokens);
        if (!string.IsNullOrWhiteSpace(metadata.RequiredFiles.BpeModel))
        {
            files.Add(metadata.RequiredFiles.BpeModel);
        }

        return files;
    }

    private static string GetArchiveRoot() => RuntimeConstants.EnsureCacheFolderPath("speech-recognition", "sherpa-onnx", "archives");

    private static string GetInstalledRoot() => RuntimeConstants.EnsureCacheFolderPath("speech-recognition", "sherpa-onnx", "installed");

    private static string GetStagingRoot() => RuntimeConstants.EnsureCacheFolderPath("speech-recognition", "sherpa-onnx", "staging");

    private static string GetManifestPath(string modelRoot) => Path.Combine(modelRoot, "everywhere-sherpa-model.json");

    private static string NormalizeSha256(string sha256) =>
        sha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? sha256["sha256:".Length..] : sha256;

    private static void SafeDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Ignore
        }
    }

    private sealed record InstalledModelManifest(
        string ModelId,
        string ArchiveSha256,
        DateTimeOffset InstalledAt,
        IReadOnlyList<string> RequiredFiles);
}
