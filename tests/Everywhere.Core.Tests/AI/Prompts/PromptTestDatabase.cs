using Everywhere.AI.Prompts.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Core.Tests.AI.Prompts;

internal sealed class PromptTestDatabase : IDisposable
{
    public TestPromptDbContextFactory Factory { get; }

    private readonly string _path;

    private PromptTestDatabase(string path)
    {
        _path = path;
        var options = new DbContextOptionsBuilder<PromptDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        Factory = new TestPromptDbContextFactory(options);
    }

    public static PromptTestDatabase Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Everywhere.Core.Tests", "Prompts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        return new PromptTestDatabase(path);
    }

    public async Task MigrateAsync()
    {
        await using var dbContext = await Factory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Best-effort cleanup; SQLite may keep a transient handle briefly on some platforms.
        }
    }
}

internal sealed class TestPromptDbContextFactory(DbContextOptions<PromptDbContext> options)
    : IDbContextFactory<PromptDbContext>
{
    public PromptDbContext CreateDbContext() => new(options);

    public ValueTask<PromptDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CreateDbContext());
}
