using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everywhere.Common;

public sealed class FileDownloadService(
    IHttpClientFactory httpClientFactory,
    ILogger<FileDownloadService> logger
) : IFileDownloadService
{
    private static readonly TimeSpan SourceProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<string> DownloadAsync(
        FileDownloadRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Sources.Count == 0)
        {
            throw new ArgumentException("At least one download source is required.", nameof(request));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath) ?? ".");
        var tempPath = request.DestinationPath + ".part";

        if (await IsExistingFileValidAsync(request.DestinationPath, request, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1d);
            return request.DestinationPath;
        }

        var tempInfo = new FileInfo(tempPath);
        if (tempInfo.Exists && request.Size is { } size && tempInfo.Length > size)
        {
            tempInfo.Delete();
        }

        var resumeOffset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;
        if (resumeOffset > 0 && await IsExistingFileValidAsync(tempPath, request, cancellationToken).ConfigureAwait(false))
        {
            File.Move(tempPath, request.DestinationPath, overwrite: true);
            progress?.Report(1d);
            return request.DestinationPath;
        }

        Exception? lastException = null;
        using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);

        var sources = await SelectSourcesAsync(httpClient, request.Sources, cancellationToken).ConfigureAwait(false);
        foreach (var source in sources)
        {
            try
            {
                await DownloadFromSourceAsync(
                    httpClient,
                    source,
                    tempPath,
                    request,
                    resumeOffset,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (!await IsExistingFileValidAsync(tempPath, request, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        // Ignore deletion failure; the file will be overwritten on the next attempt or left for manual cleanup if all sources fail.
                        logger.LogWarning(ex, "Failed to delete invalid temp file.");
                    }

                    throw new InvalidOperationException($"Downloaded file failed validation: {Path.GetFileName(request.DestinationPath)}.");
                }

                File.Move(tempPath, request.DestinationPath, overwrite: true);
                progress?.Report(1d);
                return request.DestinationPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Download from {SourceName} failed.", source.Name ?? source.Url);

                if (!File.Exists(tempPath))
                {
                    resumeOffset = 0;
                }
                else
                {
                    resumeOffset = new FileInfo(tempPath).Length;
                }
            }
        }

        throw new InvalidOperationException("All download sources failed.", lastException);
    }

    private async Task<IReadOnlyList<FileDownloadSource>> SelectSourcesAsync(
        HttpClient httpClient,
        IReadOnlyList<FileDownloadSource> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count <= 1) return sources;

        var responsive = new List<FileDownloadSource>(sources.Count);
        var untested = new List<FileDownloadSource>(sources.Count);

        foreach (var source in sources)
        {
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var probeTask = ProbeSourceAsync(httpClient, source.Url, probeCts.Token);
                var delayTask = Task.Delay(SourceProbeTimeout, cancellationToken);
                var completed = await Task.WhenAny(probeTask, delayTask).ConfigureAwait(false);

                if (completed == probeTask && await probeTask.ConfigureAwait(false))
                {
                    responsive.Add(source);
                }
                else
                {
                    await probeCts.CancelAsync().ConfigureAwait(false);
                    untested.Add(source);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Download source probe failed for {Url}.", source.Url);
                untested.Add(source);
            }
        }

        return responsive.Count == 0 ? sources : responsive.Concat(untested).ToList();
    }

    private static async Task<bool> ProbeSourceAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task DownloadFromSourceAsync(
        HttpClient httpClient,
        FileDownloadSource source,
        string tempPath,
        FileDownloadRequest downloadRequest,
        long resumeOffset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            logger.LogInformation(
                "Source ignored range request; restarting download for {FileName}.",
                Path.GetFileName(downloadRequest.DestinationPath));
            File.Delete(tempPath);
            await DownloadFromSourceAsync(httpClient, source, tempPath, downloadRequest, 0, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = downloadRequest.Size ??
            ((response.Content.Headers.ContentLength is { } contentLength) ? contentLength + resumeOffset : null);
        progress?.Report(totalBytes is > 0 ? Math.Clamp((double)resumeOffset / totalBytes.Value, 0d, 1d) : double.NaN);
        var fileMode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
        await using var writeStream = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var rateLimiter = downloadRequest.BytesPerSecondLimit is > 0 ? new TokenBucketRateLimiter(downloadRequest.BytesPerSecondLimit.Value) : null;
        var totalBytesRead = resumeOffset;
        var buffer = new byte[81920];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allowed = rateLimiter is null ?
                buffer.Length :
                await rateLimiter.AcquireAsync(buffer.Length, cancellationToken).ConfigureAwait(false);

            var bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, allowed), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0) break;

            await writeStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;

            if (rateLimiter is not null && bytesRead < allowed)
            {
                rateLimiter.ReturnUnused(allowed - bytesRead);
            }

            if (totalBytes is > 0)
            {
                progress?.Report(Math.Clamp((double)totalBytesRead / totalBytes.Value, 0d, 1d));
            }
        }

        if (totalBytes is { } expectedBytes && totalBytesRead < expectedBytes)
        {
            throw new InvalidOperationException(
                $"Download incomplete: received {totalBytesRead} of {expectedBytes} bytes for {Path.GetFileName(downloadRequest.DestinationPath)}.");
        }
    }

    private static async Task<bool> IsExistingFileValidAsync(
        string path,
        FileDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists) return false;

        if (request.Size is { } size && fileInfo.Length != size)
        {
            return false;
        }

        if (request.Sha256Digest is not { Length: > 0 } expectedDigest)
        {
            return true;
        }

        await using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha256 = await SHA256.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false);
        var actualDigest = Convert.ToHexString(sha256);
        expectedDigest = NormalizeSha256Digest(expectedDigest);
        return string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSha256Digest(string digest)
    {
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? digest[prefix.Length..] : digest;
    }
}
