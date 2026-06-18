using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Everywhere.Statistics.Database;

/// <summary>
/// EF Core context for the independent local statistics database.
/// </summary>
/// <remarks>
/// The database stores aggregatable event rows only. Chat message content remains owned by <c>ChatDbContext</c>.
/// </remarks>
public sealed class StatisticsDbContext(DbContextOptions<StatisticsDbContext> options) : DbContext(options)
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<TopicEventEntity> TopicEvents => Set<TopicEventEntity>();
    public DbSet<TurnEventEntity> TurnEvents => Set<TurnEventEntity>();
    public DbSet<ModelInvocationEventEntity> ModelInvocationEvents => Set<ModelInvocationEventEntity>();
    public DbSet<ToolInvocationEventEntity> ToolInvocationEvents => Set<ToolInvocationEventEntity>();
    public DbSet<VisualContextEventEntity> VisualContextEvents => Set<VisualContextEventEntity>();
    public DbSet<StatisticsMetadataEntity> Metadata => Set<StatisticsMetadataEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<DeviceEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceGuid).IsUnique();
            e.Property(x => x.DeviceGuid).HasMaxLength(36);
            e.Property(x => x.DisplayName).HasMaxLength(128);
        });

        builder.Entity<TopicEventEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DeviceId, x.ChatContextId }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Topic).HasMaxLength(64);
            e.HasOne<DeviceEntity>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TurnEventEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ChatContextId);
            e.HasOne<DeviceEntity>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ModelInvocationEventEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => x.ChatContextId);
            e.HasIndex(x => x.TurnEventId);
            e.Property(x => x.ModelId).HasMaxLength(255);
            e.Property(x => x.ErrorType).HasMaxLength(255);
            e.HasOne<DeviceEntity>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ToolInvocationEventEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => x.ChatContextId);
            e.HasIndex(x => x.TurnEventId);
            e.HasIndex(x => x.ModelInvocationEventId);
            e.Property(x => x.PluginKey).HasMaxLength(255);
            e.Property(x => x.FunctionName).HasMaxLength(255);
            e.HasOne<DeviceEntity>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<VisualContextEventEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ChatContextId);
            e.HasIndex(x => x.TurnEventId);
            e.HasOne<DeviceEntity>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<StatisticsMetadataEntity>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(2048);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllConstructors)]
    private class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(v => v.Ticks, v => new DateTimeOffset(v, TimeSpan.Zero));
}

/// <summary>
/// Dimension row for one installation/device that has produced statistics.
/// </summary>
public sealed class DeviceEntity
{
    public int Id { get; init; }

    /// <summary>
    /// Stable installation identifier from <see cref="Configuration.RuntimeConstants.DeviceId"/>.
    /// </summary>
    [MaxLength(36)]
    public required string DeviceGuid { get; init; }

    /// <summary>
    /// Human-readable label used when a future UI shows per-device filters.
    /// </summary>
    [MaxLength(128)]
    public string? DisplayName { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Updated whenever the recorder resolves this device row.
    /// </summary>
    public required DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// One row per locally observed chat topic. Soft-deleted chats remain counted.
/// </summary>
public sealed class TopicEventEntity
{
    public required Guid Id { get; init; }
    public required int DeviceId { get; init; }
    public required Guid ChatContextId { get; init; }

    [MaxLength(64)]
    public string? Topic { get; init; }

    /// <summary>
    /// Uses ChatContext.CreatedAt, not the first user message time.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// A user-request-level event. Retry and edit create turns; continue does not.
/// </summary>
public sealed class TurnEventEntity
{
    public required Guid Id { get; init; }
    public required int DeviceId { get; init; }
    public required Guid ChatContextId { get; init; }
    public Guid? UserChatNodeId { get; init; }
    public Guid? AssistantChatNodeId { get; init; }
    public required StatisticsTurnKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// A single model API request. Multi-step tool calling can produce multiple rows for one turn.
/// </summary>
/// <remarks>
/// Token fields are copied only from provider usage metadata. Visual context is intentionally not converted to tokens here.
/// </remarks>
public sealed class ModelInvocationEventEntity
{
    public required Guid Id { get; init; }
    public required int DeviceId { get; init; }
    public Guid? TurnEventId { get; init; }
    public Guid? ChatContextId { get; init; }
    public Guid? AssistantChatNodeId { get; init; }
    public required StatisticsModelInvocationPurpose Purpose { get; init; }

    [MaxLength(255)]
    public string? ModelId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool IsSuccess { get; set; }
    public bool IsCanceled { get; set; }

    [MaxLength(255)]
    public string? ErrorType { get; set; }

    public long InputTokenCount { get; set; }
    public long CachedInputTokenCount { get; set; }
    public long OutputTokenCount { get; set; }
    public long ReasoningTokenCount { get; set; }
    public long TotalTokenCount { get; set; }
    public double GenerationSeconds { get; set; }
}

/// <summary>
/// A single tool function call requested by the model.
/// </summary>
public sealed class ToolInvocationEventEntity
{
    public required Guid Id { get; init; }
    public required int DeviceId { get; init; }
    public Guid? TurnEventId { get; init; }
    public Guid? ModelInvocationEventId { get; init; }
    public Guid? ChatContextId { get; init; }

    /// <example>builtin.visual_context</example>
    [MaxLength(255)]
    public string? PluginKey { get; init; }

    /// <example>get_visual_tree</example>
    [MaxLength(255)]
    public string? FunctionName { get; init; }

    public required StatisticsToolInvocationStatus Status { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// Quantifies visual context capture without assigning synthetic token counts.
/// </summary>
public sealed class VisualContextEventEntity
{
    public required Guid Id { get; init; }
    public required int DeviceId { get; init; }
    public Guid? TurnEventId { get; init; }
    public Guid? ChatContextId { get; init; }
    public required StatisticsVisualContextSource Source { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Count of visual tree elements read or emitted for model context.
    /// </summary>
    public int ElementCount { get; init; }
    public int ScreenshotCount { get; set; }
    public int ImageCount { get; set; }
}

/// <summary>
/// Small key-value store for statistics database maintenance state.
/// </summary>
/// <remarks>
/// Used for one-time processes such as historical backfill versioning.
/// </remarks>
public sealed class StatisticsMetadataEntity
{
    [MaxLength(64)]
    public required string Key { get; init; }

    [MaxLength(2048)]
    public string? Value { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
