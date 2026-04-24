using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Database;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContextBase(options)
{
    public DbSet<LongTermMemoryEntity> LongTermMemories => Set<LongTermMemoryEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<LongTermMemoryEntity>(entity =>
        {
            entity.ToTable("long_term_memory");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).HasMaxLength(25 * 1024);
            entity.Property(e => e.UpdatedAt).IsRequired();
        });
    }
}

public class LongTermMemoryEntity
{
    public long Id { get; set; }

    [MaxLength(25 * 1024)]
    public string? Content { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}