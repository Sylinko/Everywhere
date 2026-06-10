namespace Everywhere.Common;

public interface IFileDownloadService
{
    Task<string> DownloadAsync(
        FileDownloadRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}