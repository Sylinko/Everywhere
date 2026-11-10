using System.ComponentModel.DataAnnotations;

namespace Everywhere.Database;

/// <summary>
/// Local metadata for content-addressed blobs (files/images).
/// The actual file path convention can be: {BlobBasePath}/{CreatedAt:yyyyMMdd}/{Sha256}.
/// </summary>
public sealed class BlobEntity
{
    /// <summary>
    /// Hex-encoded SHA-256 (lowercase). Primary key.
    /// </summary>
    [MaxLength(64)]
    public required string Sha256 { get; init; }

    /// <summary>
    /// Local file path where the blob is stored.
    /// </summary>
    [MaxLength(1024)]
    public required string LocalPath { get; init; }

    /// <summary>
    /// MIME type, e.g., "image/png".
    /// </summary>
    [MaxLength(255)]
    public required string MimeType { get; init; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Local creation time.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last access time (for LRU/GC decisions).
    /// </summary>
    public DateTimeOffset LastAccessAt { get; set; }
}