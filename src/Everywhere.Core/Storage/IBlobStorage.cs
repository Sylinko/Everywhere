using Everywhere.Database;

namespace Everywhere.Storage;

/// <summary>
/// Manages the storage and retrieval of content-addressed blobs (files, images, etc.).
/// This service handles the physical file storage and the metadata in the BlobEntity table.
/// </summary>
public interface IBlobStorage
{
    /// <summary>
    /// Creates a blob from a stream, calculates its SHA256 hash, and saves it to the blob store if it doesn't already exist.
    /// It creates or updates the corresponding BlobEntity in the database, then saves the stream to disk.
    /// </summary>
    /// <param name="content">The stream containing the blob data.</param>
    /// <param name="mimeType">The MIME type of the blob.</param>
    /// <param name="extension">The extension of the storage file. If null, it will be inferred from the MIME type or left empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The BlobEntity representing the stored blob.</returns>
    Task<BlobEntity> StorageBlobAsync(Stream content, string mimeType, string? extension = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a blob from a file path, calculates its SHA256 hash, and saves it to the blob store if it doesn't already exist.
    /// It creates or updates the corresponding BlobEntity in the database, but the original file remains unchanged.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="mimeType"></param>
    /// <param name="maxBytesSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<BlobEntity> StorageBlobAsync(
        string filePath,
        string? mimeType = null,
        long maxBytesSize = 25L * 1024 * 1024,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves blob metadata from the database by its SHA256 hash.
    /// </summary>
    /// <param name="sha256">The SHA256 hash of the blob.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The found BlobEntity, or null if not found.</returns>
    Task<BlobEntity?> QueryBlobAsync(string sha256, CancellationToken cancellationToken = default);
}