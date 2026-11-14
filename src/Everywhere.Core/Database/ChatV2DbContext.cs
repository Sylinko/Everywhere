using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Chat;
using Everywhere.Cloud;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Everywhere.Database;

/// <summary>
/// Local turn-based chat store. Payloads and topology share a transaction, not an EF object graph.
/// </summary>
[method: DynamicDependency(DynamicallyAccessedMemberTypes.AllConstructors, typeof(DateTimeOffsetToTicksConverter))]
public sealed class ChatV2DbContext(DbContextOptions<ChatV2DbContext> options) : DbContext(options)
{
    /// <summary>
    /// Local conversation metadata, including archived and session-only chats.
    /// </summary>
    public DbSet<ChatV2Entity> Chats => Set<ChatV2Entity>();

    /// <summary>
    /// Local composer drafts, including drafts without a persisted conversation.
    /// </summary>
    public DbSet<ChatInputEntity> ChatInputs => Set<ChatInputEntity>();

    /// <summary>
    /// Node topology and separately materialized message payloads.
    /// </summary>
    public DbSet<ChatV2NodeEntity> Nodes => Set<ChatV2NodeEntity>();

    /// <summary>
    /// Attachment references retained even when the local file is missing.
    /// </summary>
    public DbSet<ChatV2NodeBlobEntity> NodeBlobs => Set<ChatV2NodeBlobEntity>();

    /// <summary>
    /// Locally available attachment files and their access metadata.
    /// </summary>
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();

    /// <summary>
    /// Initialization and upgrade-notice state for this database.
    /// </summary>
    public DbSet<ChatStoreInfoEntity> StoreInfo => Set<ChatStoreInfoEntity>();

    /// <summary>
    /// Account discovery cursors and local edit counters.
    /// </summary>
    public DbSet<ChatSyncAccountEntity> SyncAccounts => Set<ChatSyncAccountEntity>();

    /// <summary>
    /// Discovered and installed heads, including cloud-only chats and retained clear/delete decisions.
    /// </summary>
    public DbSet<ChatSyncChatEntity> SyncChats => Set<ChatSyncChatEntity>();

    /// <summary>
    /// Confirmed record revision baselines, independently of current field values.
    /// </summary>
    public DbSet<ChatSyncRemoteEntity> SyncRemote => Set<ChatSyncRemoteEntity>();

    /// <summary>
    /// Unacknowledged local edit intents, committed atomically with ordinary writes.
    /// </summary>
    public DbSet<ChatSyncPendingEntity> SyncPending => Set<ChatSyncPendingEntity>();

    /// <summary>
    /// Frozen upload identities retained across interruption and restart.
    /// </summary>
    public DbSet<ChatSyncSubmissionEntity> SyncSubmissions => Set<ChatSyncSubmissionEntity>();

    /// <summary>
    /// Ordered frozen wire parts belonging to unresolved submissions.
    /// </summary>
    public DbSet<ChatSyncPartEntity> SyncParts => Set<ChatSyncPartEntity>();

    /// <summary>
    /// Defines the persisted chat graph and independently retained synchronization state.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        var recordKind = new ValueConverter<ChatSyncRecordKind, string>(kind => kind.ToStorageKey(), key => ChatSyncStorageKeys.ReadRecordKind(key));
        builder.Entity<ChatSyncAccountEntity>(entity =>
        {
            entity.ToTable("SyncAccounts");
            entity.HasKey(row => row.AccountId);
        });

        builder.Entity<ChatSyncChatEntity>(entity =>
        {
            // No Chats foreign key: remote-only heads and deletion/clear decisions
            // must survive removal of this device's graph.
            entity.ToTable("SyncChats");
            entity.HasKey(row => new { row.AccountId, row.ChatId });
            entity.Property(row => row.IsContentUnavailable).HasDefaultValue(false);
        });

        builder.Entity<ChatSyncRemoteEntity>(entity =>
        {
            entity.ToTable("SyncRemote");
            entity.HasKey(row => new { row.AccountId, row.ChatId, row.Kind, row.Key });
            entity.Property(row => row.Kind).HasConversion(recordKind);
        });

        builder.Entity<ChatSyncPendingEntity>(entity =>
        {
            entity.ToTable("SyncPending");
            entity.HasKey(row => new { row.AccountId, row.ChatId, row.Kind, row.Key });
            entity.Property(row => row.Kind).HasConversion(recordKind);
        });

        builder.Entity<ChatSyncSubmissionEntity>(entity =>
        {
            entity.ToTable("SyncSubmissions");
            entity.HasKey(row => new { row.AccountId, row.ChatId });
            entity.HasIndex(row => new { row.AccountId, row.CommitId }).IsUnique();
        });

        builder.Entity<ChatSyncPartEntity>(entity =>
        {
            entity.ToTable("SyncParts");
            entity.HasKey(row => new { row.AccountId, row.ChatId, row.Position });
            entity.HasOne<ChatSyncSubmissionEntity>().WithMany().HasForeignKey(row => new { row.AccountId, row.ChatId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChatV2Entity>(entity =>
        {
            entity.ToTable("Chats");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasIndex(row => new { row.UpdatedAt, row.Id }).HasFilter("\"ArchivedAt\" IS NULL");
            entity.Property(row => row.IsPinned).HasDefaultValue(false);
            entity.Property(row => row.IsTemporary).HasDefaultValue(false);
            entity.Property(row => row.IsSyncExcluded).HasDefaultValue(false);
            entity.HasIndex(row => new { row.IsPinned, row.UpdatedAt, row.Id }, "IX_Chats_TemporaryHistory")
                .IsDescending().HasFilter("\"IsTemporary\" = 1 AND \"ArchivedAt\" IS NULL");
            entity.HasIndex(row => new { row.IsPinned, row.UpdatedAt, row.Id }, "IX_Chats_ActiveHistory")
                .IsDescending().HasFilter("\"ArchivedAt\" IS NULL");
            entity.HasIndex(row => new { row.IsPinned, row.UpdatedAt, row.Id }, "IX_Chats_ArchivedHistory")
                .IsDescending().HasFilter("\"ArchivedAt\" IS NOT NULL");
        });

        builder.Entity<ChatInputEntity>(entity =>
        {
            // No Chats foreign key: saving unsent input must never create a history row.
            entity.ToTable("ChatInputs");
            entity.HasKey(row => row.SlotId);
            entity.Property(row => row.SlotId).ValueGeneratedNever();
        });

        builder.Entity<ChatV2NodeEntity>(entity =>
        {
            entity.ToTable("Nodes");
            entity.HasKey(row => new { row.ChatContextId, row.Id });
            entity.HasIndex(row => new { row.ChatContextId, row.TurnId });
            entity.HasIndex(row => new { row.ChatContextId, row.ParentId });
            entity.HasIndex(row => row.ChatContextId).IsUnique().HasFilter("\"ParentId\" IS NULL");
            entity.HasOne<ChatV2Entity>().WithMany().HasForeignKey(row => row.ChatContextId).OnDelete(DeleteBehavior.Cascade);
            // The Initial migration defers these three self-references until commit.
            // EF's SQLite model cannot express that clause; preserve it when rebuilding Nodes.
            entity.HasOne<ChatV2NodeEntity>().WithMany().HasForeignKey(row => new { row.ChatContextId, row.ParentId })
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ChatV2NodeEntity>().WithMany().HasForeignKey(row => new { row.ChatContextId, row.TurnId })
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ChatV2NodeEntity>().WithMany().HasForeignKey(row => new { row.ChatContextId, row.ChoiceChildId })
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<ChatV2NodeBlobEntity>(entity =>
        {
            entity.ToTable("NodeBlobs");
            entity.HasKey(row => new { row.ChatContextId, row.ChatNodeId, row.BlobSha256 });
            entity.HasIndex(row => row.BlobSha256);
            entity.HasOne<ChatV2NodeEntity>().WithMany().HasForeignKey(row => new { row.ChatContextId, row.ChatNodeId })
                .OnDelete(DeleteBehavior.Cascade);
            // No Blobs foreign key: missing local attachment files are valid references.
        });

        builder.Entity<BlobEntity>(entity =>
        {
            entity.ToTable("Blobs");
            entity.HasKey(row => row.Sha256);
            entity.HasIndex(row => row.LastAccessAt);
        });

        builder.Entity<ChatStoreInfoEntity>(entity =>
        {
            entity.ToTable("StoreInfo", table => table.HasCheckConstraint("CK_StoreInfo_Singleton", "\"Id\" = 1"));
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
        });
    }

    /// <summary>
    /// Stores all timestamps, including nullable ones, as sortable UTC ticks.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    /// <summary>
    /// Preserves the existing INTEGER timestamp format used by EF and incremental SQL writes.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllConstructors)]
    private class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks,
        value => new DateTimeOffset(value, TimeSpan.Zero));
}

/// <summary>
/// Concrete persisted metadata and local lifecycle/account state. Known metadata values live
/// only in their typed columns; unrecognized synchronization fields live only in ExtensionData.
/// </summary>
public sealed class ChatV2Entity
{
    /// <summary>
    /// Conversation identity assigned by the application or received from synchronization.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Optional title. New local edits use the shared topic length limit.
    /// </summary>
    [MaxLength(ChatMetadata.MaximumTopicLength)]
    public string? Topic { get; set; }

    /// <summary>
    /// Conversation creation time, stored as UTC ticks.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last durable content activity used for history and quota-recovery ordering.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Archive time, or null while the conversation remains in active history.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>
    /// Whether history lists place this conversation before unpinned entries.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Whether this graph is removed on the next application startup.
    /// </summary>
    public bool IsTemporary { get; set; }

    /// <summary>
    /// The account that retained this local history, independently of its current sync preference.
    /// Unattributed histories can be claimed only by an enabled account after discovery.
    /// </summary>
    // The authenticated subject is opaque and has no application-defined length limit.
    // ReSharper disable once EntityFramework.ModelValidation.UnlimitedStringLength
    public string? LocalAccountId { get; set; }

    /// <summary>
    /// An explicit stop before publication. Published stops are represented by their remote clear revision.
    /// </summary>
    public bool IsSyncExcluded { get; set; }

    /// <summary>
    /// Optional MessagePack dictionary of unknown wire keys to their original value bytes.
    /// Ordinary typed saves leave it untouched; recognized fields are stored in columns instead.
    /// </summary>
    public byte[]? ExtensionData { get; set; }
}

/// <summary>
/// A topology row and its separately loaded message payload.
/// Parent, turn, and chosen-child foreign keys are deferred until commit in the SQLite migration.
/// </summary>
public sealed class ChatV2NodeEntity
{
    /// <summary>
    /// Owning conversation; all node references are scoped to this graph.
    /// </summary>
    public Guid ChatContextId { get; set; }

    /// <summary>
    /// Stable node identity, including the graph's root.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Parent node, or null for the single root of this conversation.
    /// </summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// Starting node of this turn, or null for nodes outside a turn.
    /// </summary>
    public Guid? TurnId { get; set; }

    /// <summary>
    /// Persisted selected child, independent of the currently displayed branch.
    /// </summary>
    public Guid? ChoiceChildId { get; set; }

    /// <summary>
    /// Queryable message discriminator corresponding to the serialized payload.
    /// </summary>
    public ChatMessageKind MessageKind { get; set; }

    /// <summary>
    /// Last node covered by a completed compression summary, or null without a summary.
    /// </summary>
    public Guid? SummaryThroughNodeId { get; set; }

    /// <summary>
    /// Opaque MessagePack message bytes, loaded independently of topology.
    /// </summary>
    public required byte[] Payload { get; set; }

    /// <summary>
    /// Node creation time, stored as UTC ticks.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last persisted node change, including branch-choice changes.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Set of managed blob dependencies of a node, including embedded child chats.
/// A missing local blob row does not invalidate the message or its dependency reference.
/// </summary>
public sealed class ChatV2NodeBlobEntity
{
    /// <summary>
    /// Conversation containing the referencing node.
    /// </summary>
    public Guid ChatContextId { get; set; }

    /// <summary>
    /// Node that retains this attachment dependency.
    /// </summary>
    public Guid ChatNodeId { get; set; }

    /// <summary>
    /// Content-addressed attachment identity, whether or not its file is available locally.
    /// </summary>
    [MaxLength(64)]
    public required string BlobSha256 { get; set; }
}

/// <summary>
/// Records completed initialization and the independent upgrade-notice acknowledgment.
/// The import and this singleton are committed together.
/// </summary>
public sealed class ChatStoreInfoEntity
{
    /// <summary>
    /// Fixed singleton key; never generated by the database.
    /// </summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// Time the initial store creation or legacy import committed.
    /// </summary>
    public DateTimeOffset InitializedAt { get; set; }

    /// <summary>
    /// Time a legacy database was imported, or null for a new installation.
    /// </summary>
    public DateTimeOffset? LegacyImportedAt { get; set; }

    /// <summary>
    /// Time the user acknowledged the local upgrade notice, or null until acknowledged.
    /// </summary>
    public DateTimeOffset? UpgradeNoticeAcknowledgedAt { get; set; }
}