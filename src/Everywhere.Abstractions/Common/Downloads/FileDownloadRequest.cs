namespace Everywhere.Common;

public sealed record FileDownloadRequest(
    string DestinationPath,
    IReadOnlyList<FileDownloadSource> Sources,
    long? Size = null,
    string? Sha256Digest = null,
    long? BytesPerSecondLimit = null);