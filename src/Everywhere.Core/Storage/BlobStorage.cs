using System.Security.Cryptography;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

public class BlobStorage(IDbContextFactory<ChatDbContext> dbFactory) : IBlobStorage
{
    private readonly string _blobBasePath = RuntimeConstants.EnsureWritableDataFolderPath("blob");

    public Task<BlobEntity> StorageBlobAsync(
        Stream content,
        string mimeType,
        string? extension = null,
        CancellationToken cancellationToken = default) =>
        StorageBlobAsync(content, null, mimeType, extension, cancellationToken);

    public async Task<BlobEntity> StorageBlobAsync(
        string filePath,
        string? mimeType = null,
        long maxBytesSize = 26214400,
        CancellationToken cancellationToken = default)
    {
        if (filePath.Length > 1024)
        {
            throw new PathTooLongException($"Blob local path is too long: {filePath}");
        }

        await using var stream = File.OpenRead(filePath);
        if (stream.Length > maxBytesSize)
        {
            throw new OverflowException($"File size exceeds the maximum allowed size of {maxBytesSize} bytes.");
        }

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
            // Blob already exists, just update its access time
            blobEntity.LastAccessAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return blobEntity;
        }

        if (localPath is null)
        {
            // Organize blobs by date to avoid having too many files in one directory
            var datePath = now.ToString("yyyyMMdd");
            var blobDirectory = Path.Combine(_blobBasePath, datePath);
            Directory.CreateDirectory(blobDirectory);

            extension ??= FileUtilities.GetExtensionByMimeType(mimeType);
            var fileName = extension.IsNullOrWhiteSpace() ? sha256String : $"{sha256String}.{extension.TrimStart('.')}";
            localPath = Path.Combine(_blobBasePath, datePath, fileName);
            if (localPath.Length > 1024)
            {
                throw new PathTooLongException($"Blob local path is too long: {localPath}");
            }

            await using (var fileStream = File.Create(localPath))
            {
                content.Seek(0, SeekOrigin.Begin);
                await content.CopyToAsync(fileStream, cancellationToken);
            }
        }

        // Blob doesn't exist, so save it
        blobEntity = new BlobEntity
        {
            Sha256 = sha256String,
            LocalPath = localPath,
            MimeType = mimeType,
            Size = content.Length,
            CreatedAt = now,
            LastAccessAt = now
        };

        db.Blobs.Add(blobEntity);
        await db.SaveChangesAsync(cancellationToken);

        return blobEntity;
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