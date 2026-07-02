using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI.Prompts.Database;

/// <summary>
/// EF Core context for the isolated Prompt Manager database.
/// </summary>
/// <remarks>
/// Prompt Manager uses its own <c>prompt.db</c> instead of sharing chat or statistics storage. The
/// built-in default prompt is intentionally absent from this context; only user-created or migrated
/// prompts are stored here.
/// </remarks>
[method: DynamicDependency(DynamicallyAccessedMemberTypes.AllConstructors, typeof(DateTimeOffsetToTicksConverter))]
public sealed class PromptDbContext(DbContextOptions<PromptDbContext> options) : DbContext(options)
{
    /// <summary>
    /// User prompt rows stored in <c>prompt.db</c>.
    /// </summary>
    public DbSet<PromptEntity> Prompts => Set<PromptEntity>();

    /// <summary>
    /// Defines the persisted prompt schema owned by Prompt Manager.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<PromptEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UpdatedAt);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Template).IsRequired();
            e.Property(x => x.Source).HasConversion<int>();
        });
    }

    /// <summary>
    /// Stores <see cref="DateTimeOffset"/> values as UTC ticks for SQLite compatibility.
    /// </summary>
    /// <remarks>
    /// SQLite has no native <see cref="DateTimeOffset"/> type. Using ticks mirrors the existing
    /// project pattern and keeps ordering/filtering stable without culture-sensitive string formats.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    /// <summary>
    /// Converts <see cref="DateTimeOffset"/> values to UTC ticks for EF.
    /// </summary>
    /// <remarks>
    /// The dynamic dependency above keeps the converter constructible in trimmed builds where EF may
    /// create converters through reflection.
    /// </remarks>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllConstructors)]
    private class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
}

/// <summary>
/// Applies Prompt Manager database migrations during application startup.
/// </summary>
/// <remarks>
/// This initializer only ensures the database schema exists. Post-settings migration of old
/// assistant prompt strings belongs to a later initializer ordered after
/// <see cref="AsyncInitializerIndex.Settings"/>.
/// </remarks>
public sealed class PromptDbInitializer(IDbContextFactory<PromptDbContext> dbFactory, ILogger<PromptDbInitializer> logger) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

    public async Task InitializeAsync()
    {
        logger.LogInformation("Initializing prompt database...");

        await using var dbContext = await dbFactory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
    }
}

/// <summary>
/// Persisted prompt row.
/// </summary>
/// <remarks>
/// This entity is deliberately separate from <see cref="PromptDefinition"/> so EF-specific concerns
/// such as column attributes, migrations, and value converters do not leak into the application
/// service contract.
/// </remarks>
public sealed class PromptEntity
{
    public required Guid Id { get; init; }

    [MaxLength(256)]
    public string? Name { get; set; }

    // ReSharper disable once EntityFramework.ModelValidation.UnlimitedStringLength
    public required string Template { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }

    public PromptSource Source { get; set; }

    /// <summary>
    /// Opaque authoring metadata, usually a MessagePack payload such as <see cref="PromptRecipeSnapshot"/>.
    /// </summary>
    public byte[]? MetadataPayload { get; set; }
}