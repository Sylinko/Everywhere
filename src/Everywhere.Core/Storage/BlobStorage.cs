using System.Globalization;
using System.Security.Cryptography;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

public class BlobStorage(IDbContextFactory<ChatV2DbContext> dbFactory) : IBlobStorage
{
    /// <summary>
    /// Maximum encoded size of a file copied into the blob store.
    /// </summary>
    public const long MaximumCopiedBlobSizeInBytes = 25L * 1024 * 1024;

    private readonly string _blobBasePath = RuntimeConstants.EnsureWritableDataFolderPath("blob");

    public Task<BlobEntity> StorageBlobAsync(
        Stream content,
        string mimeType,
        string? extension = null,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > MaximumCopiedBlobSizeInBytes)
        {
            throw new OverflowException($"Blob size exceeds the maximum allowed size of {MaximumCopiedBlobSizeInBytes} bytes.");
        }

        return StorageBlobAsync(content, null, mimeType, extension, cancellationToken);
    }

    public async Task<BlobEntity> StorageBlobAsync(
        string filePath,
        string? mimeType = null,
        CancellationToken cancellationToken = default)
    {
        if (filePath.Length > 1024)
        {
            throw new PathTooLongException($"Blob local path is too long: {filePath}");
        }

        await using var stream = File.OpenRead(filePath);
        mimeType = await FileUtilities.EnsureMimeTypeAsync(mimeType, filePath, cancellationToken);
        return await StorageBlobAsync(stream, filePath, mimeType, Path.GetExtension(filePath), cancellationToken);
    }

    private async Task<BlobEntity> StorageBlobAsync(
        Stream content,
        string? localPath,
        string mimeType,
        string? extension,
        CancellationToken cancellationToken)
    {
        content.Seek(0, SeekOrigin.Begin);
        var sha256Bytes = await SHA256.HashDataAsync(content, cancellationToken);
        var sha256String = Convert.ToHexString(sha256Bytes).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var blobEntity = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha256String, cancellationToken);
        if (blobEntity is not null)
        {
            blobEntity.LastAccessAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (localPath is not null ||
                PathUtilities.IsResolvedPathInsideDirectory(blobEntity.LocalPath, _blobBasePath) && File.Exists(blobEntity.LocalPath))
            {
                return blobEntity;
            }

            // A user attachment can register the hash by reference. A stream caller needs
            // a snapshot, not the mutable external path that registered those bytes.
            return CreateEntity(await CopyBlobAsync(blobEntity.CreatedAt), blobEntity.CreatedAt);
        }

        localPath ??= await CopyBlobAsync(now);
        blobEntity = CreateEntity(localPath, now);
        db.Blobs.Add(blobEntity);
        await db.SaveChangesAsync(cancellationToken);
        return blobEntity;

        async Task<string> CopyBlobAsync(DateTimeOffset createdAt)
        {
            // Organize blobs by date to avoid having too many files in one directory
            var datePath = createdAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var blobDirectory = Path.Combine(_blobBasePath, datePath);
            Directory.CreateDirectory(blobDirectory);

            extension ??= FileUtilities.GetExtensionByMimeType(mimeType);
            var fileName = extension.IsNullOrWhiteSpace() ? sha256String : $"{sha256String}.{extension.TrimStart('.')}";
            var path = Path.Combine(blobDirectory, fileName);
            if (path.Length > 1024)
            {
                throw new PathTooLongException($"Blob local path is too long: {path}");
            }

            if (File.Exists(path))
            {
                return path;
            }

            // Publish only a complete copy at the content-addressed path.
            var temporaryPath = Path.Combine(blobDirectory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var fileStream = File.Create(temporaryPath))
                {
                    content.Seek(0, SeekOrigin.Begin);
                    await content.CopyToAsync(fileStream, cancellationToken);
                }

                File.Move(temporaryPath, path, overwrite: true);
                return path;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        BlobEntity CreateEntity(string path, DateTimeOffset createdAt)
        {
            return new BlobEntity
            {
                Sha256 = sha256String,
                LocalPath = path,
                MimeType = mimeType,
                Size = content.Length,
                CreatedAt = createdAt,
                LastAccessAt = now
            };
        }
    }

    public async Task<BlobEntity?> QueryBlobAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Sha256 == sha256, cancellationToken);
        if (blob is not null)
        {
            blob.LastAccessAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return blob;
    }
}