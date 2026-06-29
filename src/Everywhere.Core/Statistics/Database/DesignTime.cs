#if DEBUG

using Everywhere.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Everywhere.Statistics.Database;

/// <summary>
/// Design-time factory used by EF tooling when creating statistics database migrations.
/// </summary>
public class StatisticsDbContextFactory : IDesignTimeDbContextFactory<StatisticsDbContext>
{
    /// <summary>
    /// Creates a statistics context pointing at the normal local statistics database path.
    /// </summary>
    public StatisticsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<StatisticsDbContext>();
        var dbPath = RuntimeConstants.GetDatabasePath("statistics.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        return new StatisticsDbContext(optionsBuilder.Options);
    }
}

#endif
