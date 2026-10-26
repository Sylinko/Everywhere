#if DEBUG

using Everywhere.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Everywhere.Database;

public class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ChatDbContext>();
        var dbPath = RuntimeConstants.GetDatabasePath("chat.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        return new ChatDbContext(optionsBuilder.Options);
    }
}

/// <summary>
/// Creates the v2 model for migration scaffolding without opening application data.
/// </summary>
public sealed class ChatV2DbContextFactory : IDesignTimeDbContextFactory<ChatV2DbContext>
{
    public ChatV2DbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatV2DbContext>().UseSqlite("Data Source=:memory:").Options;
        return new ChatV2DbContext(options);
    }
}

#endif