using Everywhere.Database;
using Everywhere.Statistics.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Core.Tests.Statistics;

internal sealed class StatisticsTestDatabase : IDisposable
{
    public StatisticsDbContextFactory Factory { get; }

    private readonly string _path;

    private StatisticsTestDatabase(string path)
    {
        _path = path;
        var options = new DbContextOptionsBuilder<StatisticsDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        Factory = new StatisticsDbContextFactory(options);

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public static StatisticsTestDatabase Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Everywhere.Core.Tests", "Statistics");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        return new StatisticsTestDatabase(path);
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

internal sealed class StatisticsDbContextFactory(DbContextOptions<StatisticsDbContext> options)
    : IDbContextFactory<StatisticsDbContext>
{
    public StatisticsDbContext CreateDbContext() => new(options);

    public ValueTask<StatisticsDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CreateDbContext());
}

internal sealed class ChatTestDatabase : IDisposable
{
    public ChatV2DbContextFactory Factory { get; }

    private readonly string _path;

    private ChatTestDatabase(string path)
    {
        _path = path;
        var options = new DbContextOptionsBuilder<ChatV2DbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        Factory = new ChatV2DbContextFactory(options);

        using var db = Factory.CreateDbContext();
        db.Database.Migrate();
    }

    public static ChatTestDatabase Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Everywhere.Core.Tests", "Chat");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.db");
        return new ChatTestDatabase(path);
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

internal sealed class ChatV2DbContextFactory(DbContextOptions<ChatV2DbContext> options)
    : IDbContextFactory<ChatV2DbContext>
{
    public ChatV2DbContext CreateDbContext() => new(options);

    public ValueTask<ChatV2DbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CreateDbContext());
}
