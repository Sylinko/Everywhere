#if DEBUG

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Everywhere.AI.Prompts.Database;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> to create <see cref="PromptDbContext"/>.
/// </summary>
/// <remarks>
/// The factory points EF tooling at a temporary SQLite file instead of resolving runtime services.
/// Runtime registration remains in dependency injection and uses <c>RuntimeConstants.GetDatabasePath</c>.
/// </remarks>
public sealed class PromptDbContextFactory : IDesignTimeDbContextFactory<PromptDbContext>
{
    public PromptDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PromptDbContext>();
        var dbPath = Path.Combine(Path.GetTempPath(), "Everywhere.Prompt.DesignTime.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        return new PromptDbContext(optionsBuilder.Options);
    }
}

#endif