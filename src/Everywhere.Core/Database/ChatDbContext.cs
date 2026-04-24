using System.ComponentModel.DataAnnotations;
using Everywhere.Cloud;
using Everywhere.Common;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Database;

/// <summary>
/// EF Core DbContext for local chat storage.
/// </summary>
public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContextBase(options)
{
    /// <summary>
    /// Indicates whether a synchronization operation is currently in progress.
    /// This flag can be used to prevent <see cref="SyncSaveChangesInterceptor"/> modifications during sync.
    /// </summary>
    public bool IsSyncing { get; set; }

    public DbSet<ChatContextEntity> Chats => Set<ChatContextEntity>();
    public DbSet<ChatNodeEntity> Nodes => Set<ChatNodeEntity>();
    public DbSet<NodeBlobEntity> NodeBlobs => Set<NodeBlobEntity>();
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();
    public DbSet<CloudSyncMetadataEntity> SyncMetadata => Set<CloudSyncMetadataEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Chat context
        builder.Entity<ChatContextEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UpdatedAt);
            // Index to support cloud sync
            e.HasIndex(x => x.LocalSyncVersion);
        });

        // Chat node (message tree node)
        builder.Entity<ChatNodeEntity>(e =>
        {
            // Composite PK makes (ChatContextId, ID) the unique key for a node.
            e.HasKey(x => new { ChatId = x.ChatContextId, NodeId = x.Id });

            // Stable sibling ordering by InsertKey under the same parent.
            e.HasIndex(x => new { ChatId = x.ChatContextId, x.ParentId, x.Id }).IsUnique();

            // Filter by deletion flag.
            e.HasIndex(x => new { ChatId = x.ChatContextId, x.IsDeleted });

            // Optionally speed up "follow current branch" by chosen child id:
            e.HasIndex(x => new { ChatId = x.ChatContextId, x.ChoiceChildId });

            // Index to support cloud sync
            e.HasIndex(x => x.LocalSyncVersion);

            e.Property(x => x.Payload).IsRequired();
        });

        // Node-to-blob association (multiple attachments per node, ordered)
        builder.Entity<NodeBlobEntity>(e =>
        {
            // PK includes Index to allow multiple blobs per node and keep order.
            e.HasKey(x => new { ChatId = x.ChatContextId, NodeId = x.ChatNodeId, x.Index });

            // Fast lookup by blob id for GC/existence checks.
            e.HasIndex(x => x.BlobSha256);

            // FK to node; cascade on delete.
            e.HasOne<ChatNodeEntity>()
                .WithMany()
                .HasForeignKey(x => new { ChatId = x.ChatContextId, NodeId = x.ChatNodeId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Local blob metadata (content-addressed)
        builder.Entity<BlobEntity>(e =>
        {
            e.HasKey(x => x.Sha256);
            e.HasIndex(x => x.LastAccessAt);
        });

        builder.Entity<CloudSyncMetadataEntity>(e =>
        {
            e.HasKey(x => x.Id);
        });
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(new SyncSaveChangesInterceptor());
    }
}

public class ChatDbInitializer(IDbContextFactory<ChatDbContext> dbFactory, ILogger<ChatDbInitializer> logger) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

    public async Task InitializeAsync()
    {
        await using var dbContext = await dbFactory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();

        await EnsureSyncMetadataAsync(dbContext);
        await EnsureRootNodeIdsMigratedAsync(dbContext);
    }

    private async Task EnsureSyncMetadataAsync(ChatDbContext dbContext)
    {
        var meta = await dbContext.Set<CloudSyncMetadataEntity>().FirstOrDefaultAsync(m => m.Id == CloudSyncMetadataEntity.SingletonId);
        if (meta is not null) return;

        logger.LogInformation("Initializing sync metadata...");

        meta = new CloudSyncMetadataEntity
        {
            Id = CloudSyncMetadataEntity.SingletonId,
            LocalVersion = 0,
            LastPushedVersion = -1,
            LastPulledVersion = -1
        };
        dbContext.SyncMetadata.Add(meta);
        await dbContext.SaveChangesAsync();
    }

    private async Task EnsureRootNodeIdsMigratedAsync(ChatDbContext dbContext)
    {
        // 1. Check if there are any legacy root nodes (Id == Guid.Empty)
        var hasLegacyRootNodes = await dbContext.Nodes.AnyAsync(x => x.Id == Guid.Empty);
        if (!hasLegacyRootNodes) return;

        logger.LogInformation("Migrating legacy Root Nodes (Guid.Empty) to Version-0 deterministic IDs...");

        // 2. Get all ChatContextIds that need migration
        // Note: Only select IDs to avoid loading entire entities into memory, saving IO
        var targetIds = await dbContext.Nodes
            .AsNoTracking()
            .Where(x => x.Id == Guid.Empty)
            .Select(x => x.ChatContextId)
            .ToListAsync();

        // 3. Start migration
        // It's recommended to use ExecuteUpdateAsync (EF Core 7+), as it generates SQL directly,
        // avoiding the "Load -> Modify PK -> Save" operation that would cause errors
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            foreach (var chatId in targetIds)
            {
                var oldRootId = Guid.Empty;
                var newRootId = Guid.CreateVersion7().SetVersion(0);

                // A. First update all child nodes (where ParentId points to old Root, change to new Root)
                await dbContext.Nodes
                    .Where(n => n.ChatContextId == chatId && n.ParentId == oldRootId)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.ParentId, newRootId));

                // B. Update the Root node's primary key itself
                // Note: Directly modifying PKs is not allowed in EF Core, but ExecuteUpdate bypasses this via SQL, so it's feasible.
                // The WHERE clause must include all primary key columns (ChatContextId, Id)
                await dbContext.Nodes
                    .Where(n => n.ChatContextId == chatId && n.Id == oldRootId)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.Id, newRootId));
            }

            await transaction.CommitAsync();
            logger.LogInformation("Successfully migrated {Count} root nodes.", targetIds.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to migrate root nodes. Data remains in legacy state.");
            throw;
        }
    }
}

/// <summary>
/// Base class for all cloud-syncable entities for MessagePack polymorphic serialization.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
[Union(0, typeof(ChatContextEntity))]
[Union(1, typeof(ChatNodeEntity))]
public abstract partial class CloudSyncableEntity : ICloudSyncable
{
    /// <summary>
    /// Node Id (Guid v7).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Soft-delete flag. When true, the context should be hidden from normal listings but retained for sync/restore.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <inheritdoc/>
    public long LocalSyncVersion { get; set; }
}

/// <summary>
/// Top-level chat context (a conversation).
/// This row holds context-wide metadata used for listing, sorting, and synchronization.
/// Messages/turns are stored as tree nodes in <see cref="ChatNodeEntity"/>.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class ChatContextEntity : CloudSyncableEntity
{
    /// <summary>
    /// Creation time (local clock). Used for sorting; not authoritative for sync conflict resolution.
    /// </summary>
    [MessagePack.Key(0)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last modified time (local clock). Bump on any change within this context to support LWW at context level.
    /// </summary>
    [MessagePack.Key(1)]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Optional short title/topic for this chat. Limited to 64 chars.
    /// </summary>
    [MaxLength(64)]
    [MessagePack.Key(2)]
    public string? Topic { get; set; }
}

/// <summary>
/// A node in the chat message tree (root + alternating user/assistant/tool messages with branches).
/// Stores the serialized message payload (MessagePack).
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class ChatNodeEntity : CloudSyncableEntity
{
    /// <summary>
    /// Parent chat context Id (Guid v7).
    /// </summary>
    [MessagePack.Key(0)]
    public required Guid ChatContextId { get; init; }

    /// <summary>
    /// Parent node ID. Null for the root node.
    /// </summary>
    [MessagePack.Key(1)]
    public Guid? ParentId { get; set; }

    /// <summary>
    /// The "chosen" child of this node (persisted as ID to avoid index shifting on concurrent insert).
    /// Map to in-memory ChoiceIndex by resolving children ordered by Id.
    /// Null means no child is chosen.
    /// </summary>
    [MessagePack.Key(2)]
    public Guid? ChoiceChildId { get; set; }

    /// <summary>
    /// Serialized message payload (MessagePack binary of ChatMessage).
    /// </summary>
    [MessagePack.Key(3)]
    public required byte[] Payload { get; set; }

    /// <summary>
    /// Role/author of the message (e.g., "system", "assistant", "user", "action", "tool").
    /// </summary>
    [MaxLength(10)]
    [MessagePack.Key(4)]
    public string? Author { get; set; }

    /// <summary>
    /// Local creation time of this node (when first inserted).
    /// </summary>
    [MessagePack.Key(5)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Local last modified time (bump on payload update, branching changes that affect this node, etc.).
    /// </summary>
    [MessagePack.Key(6)]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Associates a chat node with an attachment blob (content-addressed by SHA-256).
/// Multiple attachments per node are supported and preserved in the original order via <see cref="Index"/>.
/// </summary>
public sealed class NodeBlobEntity
{
    /// <summary>
    /// Parent chat context ID.
    /// </summary>
    public required Guid ChatContextId { get; init; }

    /// <summary>
    /// Chat node ID to which the blob is attached.
    /// </summary>
    public required Guid ChatNodeId { get; init; }

    /// <summary>
    /// Zero-based order of this attachment in the node.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Hex-encoded SHA-256 (lowercase). This is the content address and storage key.
    /// </summary>
    [MaxLength(64)]
    public required string BlobSha256 { get; init; }
}

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

/// <summary>
/// A singleton metadata entity used to track the global synchronization state of the local database.
/// </summary>
public sealed class CloudSyncMetadataEntity
{
    public const int SingletonId = 1;

    /// <summary>
    /// The unique identifier for the metadata row. Typically set to a constant "1" to ensure a singleton.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Key]
    public required int Id { get; set; }

    /// <summary>
    /// The current global version of the local database.
    /// This value increments strictly whenever any <see cref="ICloudSyncable"/> entity is modified.
    /// </summary>
    public long LocalVersion { get; set; }

    /// <summary>
    /// The previous successfully pushed local version number.
    /// </summary>
    public long LastPushedVersion { get; set; }

    /// <summary>
    /// The last version number confirmed by the remote server.
    /// </summary>
    public long LastPulledVersion { get; set; }

    /// <summary>
    /// The timestamp of the last successful synchronization attempt.
    /// Mostly for UI display purposes (e.g., "Last synced: 2 mins ago").
    /// </summary>
    public DateTimeOffset? LastSyncAt { get; set; }
}