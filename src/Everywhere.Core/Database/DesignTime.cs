#if DEBUG

using Everywhere.Configuration;
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

public class MemoryDbContextFactory : IDesignTimeDbContextFactory<MemoryDbContext>
{
    public MemoryDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MemoryDbContext>();
        var dbPath = RuntimeConstants.GetDatabasePath("memory.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        return new MemoryDbContext(optionsBuilder.Options);
    }
}

#endif