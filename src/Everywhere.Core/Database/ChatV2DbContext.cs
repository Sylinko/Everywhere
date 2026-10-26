using Everywhere.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Everywhere.Database;

/// <summary>
/// Local turn-based chat store. Payloads and topology share a transaction, not an EF object graph.
/// </summary>
public sealed class ChatV2DbContext(DbContextOptions<ChatV2DbContext> options) : DbContext(options)
{
    public DbSet<ChatV2Entity> Chats => Set<ChatV2Entity>();
    public DbSet<ChatV2NodeEntity> Nodes => Set<ChatV2NodeEntity>();
    public DbSet<ChatV2NodeBlobEntity> NodeBlobs => Set<ChatV2NodeBlobEntity>();
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();
    public DbSet<ChatStoreInfoEntity> StoreInfo => Set<ChatStoreInfoEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ChatV2Entity>(entity =>
        {
            entity.ToTable("Chats");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.UpdatedAt, row.Id }).HasFilter("\"ArchivedAt\" IS NULL");
            entity.HasIndex(row => new { row.ArchivedAt, row.Id }).HasFilter("\"ArchivedAt\" IS NOT NULL");
        });

        builder.Entity<ChatV2NodeEntity>(entity =>
        {
            entity.ToTable("Nodes");
            entity.HasKey(row => new { row.ChatContextId, row.Id });
            entity.HasIndex(row => new { row.ChatContextId, row.TurnId });
            entity.HasIndex(row => new { row.ChatContextId, row.ParentId, row.SiblingOrder })
                .IsUnique().HasFilter("\"ParentId\" IS NOT NULL");
            entity.HasIndex(row => row.ChatContextId).IsUnique().HasFilter("\"ParentId\" IS NULL");
            entity.HasOne<ChatV2Entity>().WithMany().HasForeignKey(row => row.ChatContextId).OnDelete(DeleteBehavior.Cascade);
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

        var converter = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(converter);
                }
            }
        }
    }
}

/// <summary>
/// Durable conversation metadata. Archiving never changes node topology or payloads.
/// </summary>
public sealed class ChatV2Entity
{
    public Guid Id { get; set; }
    public string? Topic { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// A topology row and its separately loaded message payload.
/// Parent, turn, and chosen-child foreign keys are deferred until commit in the SQLite migration.
/// </summary>
public sealed class ChatV2NodeEntity
{
    public Guid ChatContextId { get; set; }
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? TurnId { get; set; }
    public long SiblingOrder { get; set; }
    public Guid? ChoiceChildId { get; set; }
    public ChatMessageKind MessageKind { get; set; }
    public Guid? SummaryThroughNodeId { get; set; }
    public required byte[] Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Set of managed blob dependencies of a node, including embedded child chats.
/// A missing local blob row does not invalidate the message or its dependency reference.
/// </summary>
public sealed class ChatV2NodeBlobEntity
{
    public Guid ChatContextId { get; set; }
    public Guid ChatNodeId { get; set; }
    public required string BlobSha256 { get; set; }
}

/// <summary>
/// Records completed initialization and the independent upgrade-notice acknowledgment.
/// The import and this singleton are committed together.
/// </summary>
public sealed class ChatStoreInfoEntity
{
    public int Id { get; set; } = 1;
    public DateTimeOffset InitializedAt { get; set; }
    public DateTimeOffset? LegacyImportedAt { get; set; }
    public DateTimeOffset? UpgradeNoticeAcknowledgedAt { get; set; }
}