using Everywhere.Chat.Plugins;
using Everywhere.Configuration;
using Everywhere.Skills;
using Everywhere.Statistics.Database;
using Microsoft.EntityFrameworkCore;
using ZLinq;

namespace Everywhere.Statistics;

/// <summary>
/// Queries aggregated local statistics for the home dashboard.
/// </summary>
public sealed class StatisticsService(
    IDbContextFactory<StatisticsDbContext> dbFactory,
    IChatPluginManager chatPluginManager,
    ISkillManager skillManager,
    Settings settings
) : IStatisticsService
{
    /// <inheritdoc />
    public async Task<StatisticsOverview> GetOverviewAsync(
        StatisticsRange range,
        StatisticsDeviceScope deviceScope = StatisticsDeviceScope.AllDevices,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deviceId = await ResolveDeviceIdAsync(db, deviceScope, cancellationToken);

        var topicsQuery = ApplyDeviceScope(db.TopicEvents.AsNoTracking(), deviceId);
        topicsQuery = topicsQuery.Where(x => x.CreatedAt >= range.Start && x.CreatedAt < range.End);
        var topicCount = await topicsQuery.Select(x => x.ChatContextId).Distinct().LongCountAsync(cancellationToken);

        var turnsQuery = ApplyDeviceScope(db.TurnEvents.AsNoTracking(), deviceId)
            .Where(x => x.CreatedAt >= range.Start && x.CreatedAt < range.End);
        var turnCount = await turnsQuery.LongCountAsync(cancellationToken);

        var tokenSummary = await GetTokenSummaryAsync(range, deviceScope, cancellationToken);

        var visualQuery = ApplyDeviceScope(db.VisualContextEvents.AsNoTracking(), deviceId)
            .Where(x => x.CreatedAt >= range.Start && x.CreatedAt < range.End);
        var visualContextEventCount = await visualQuery.LongCountAsync(cancellationToken);
        var visualElementCount = await visualQuery.SumAsync(x => (long)x.ElementCount, cancellationToken);
        var screenshotCount = await visualQuery.SumAsync(x => (long)x.ScreenshotCount, cancellationToken);
        var imageCount = await visualQuery.SumAsync(x => (long)x.ImageCount, cancellationToken);
        var visualContextBytes = await visualQuery.SumAsync(x => x.ByteCount, cancellationToken);

        var toolQuery = ApplyDeviceScope(db.ToolInvocationEvents.AsNoTracking(), deviceId)
            .Where(x => x.StartedAt >= range.Start && x.StartedAt < range.End);
        var toolInvocationCount = await toolQuery.LongCountAsync(cancellationToken);

        return new StatisticsOverview(
            topicCount,
            turnCount,
            tokenSummary,
            visualContextEventCount,
            visualElementCount,
            screenshotCount,
            imageCount,
            visualContextBytes,
            toolInvocationCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IStatisticsHeatmapDay>> GetHeatmapAsync(
        StatisticsHeatmapMetric metric,
        int months,
        StatisticsDeviceScope deviceScope = StatisticsDeviceScope.AllDevices,
        CancellationToken cancellationToken = default)
    {
        var monthCount = Math.Clamp(months, 1, 24);
        var today = DateTimeOffset.Now.Date;
        var startLocal = new DateTimeOffset(new DateTime(today.Year, today.Month, 1), DateTimeOffset.Now.Offset).AddMonths(1 - monthCount);
        var endLocal = new DateTimeOffset(today.AddDays(1), DateTimeOffset.Now.Offset);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deviceId = await ResolveDeviceIdAsync(db, deviceScope, cancellationToken);

        if (metric == StatisticsHeatmapMetric.Tokens)
        {
            return (await ApplyDeviceScope(db.ModelInvocationEvents.AsNoTracking(), deviceId)
                    .Where(x => x.StartedAt >= startLocal && x.StartedAt < endLocal)
                    .Select(x => new
                    {
                        CreatedAt = x.StartedAt,
                        x.InputTokenCount,
                        x.OutputTokenCount,
                        x.CachedInputTokenCount
                    })
                    .ToListAsync(cancellationToken))
                .AsValueEnumerable()
                .GroupBy(x => ToLocalDate(x.CreatedAt))
                .Select(IStatisticsHeatmapDay (x) => new StatisticsTokenHeatmapDay(
                    x.Key,
                    x.Sum(v => v.InputTokenCount),
                    x.Sum(v => v.OutputTokenCount),
                    x.Sum(v => v.CachedInputTokenCount)))
                .OrderBy(x => x.Date)
                .ToList();
        }

        IEnumerable<(DateOnly Date, long Value)> rows = metric switch
        {
            StatisticsHeatmapMetric.Topics => (await ApplyDeviceScope(db.TopicEvents.AsNoTracking(), deviceId)
                    .Where(x => x.CreatedAt >= startLocal && x.CreatedAt < endLocal)
                    .Select(x => new { x.CreatedAt, Value = 1L })
                    .ToListAsync(cancellationToken))
                .Select(x => (ToLocalDate(x.CreatedAt), x.Value)),
            StatisticsHeatmapMetric.Turns => (await ApplyDeviceScope(db.TurnEvents.AsNoTracking(), deviceId)
                    .Where(x => x.CreatedAt >= startLocal && x.CreatedAt < endLocal)
                    .Select(x => new { x.CreatedAt, Value = 1L })
                    .ToListAsync(cancellationToken))
                .Select(x => (ToLocalDate(x.CreatedAt), x.Value)),
            StatisticsHeatmapMetric.VisualContext => (await ApplyDeviceScope(db.VisualContextEvents.AsNoTracking(), deviceId)
                    .Where(x => x.CreatedAt >= startLocal && x.CreatedAt < endLocal)
                    .Select(x => new { x.CreatedAt, x.ElementCount })
                    .ToListAsync(cancellationToken))
                .Select(x => (ToLocalDate(x.CreatedAt), (long)x.ElementCount)),
            StatisticsHeatmapMetric.ToolUsage => (await ApplyDeviceScope(db.ToolInvocationEvents.AsNoTracking(), deviceId)
                    .Where(x => x.StartedAt >= startLocal && x.StartedAt < endLocal)
                    .Select(x => new { CreatedAt = x.StartedAt, Value = 1L })
                    .ToListAsync(cancellationToken))
                .Select(x => (ToLocalDate(x.CreatedAt), x.Value)),
            _ => []
        };

        return rows
            .AsValueEnumerable()
            .GroupBy(x => x.Date)
            .Select(IStatisticsHeatmapDay (x) => new StatisticsSimpleHeatmapDay(x.Key, x.Sum(v => v.Value)))
            .OrderBy(x => x.Date)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<StatisticsTokenSummary> GetTokenSummaryAsync(
        StatisticsRange range,
        StatisticsDeviceScope deviceScope = StatisticsDeviceScope.AllDevices,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deviceId = await ResolveDeviceIdAsync(db, deviceScope, cancellationToken);
        var query = ApplyDeviceScope(db.ModelInvocationEvents.AsNoTracking(), deviceId)
            .Where(x => x.StartedAt >= range.Start && x.StartedAt < range.End);

        return new StatisticsTokenSummary(
            await query.SumAsync(x => x.InputTokenCount, cancellationToken),
            await query.SumAsync(x => x.CachedInputTokenCount, cancellationToken),
            await query.SumAsync(x => x.OutputTokenCount, cancellationToken),
            await query.SumAsync(x => x.ReasoningTokenCount, cancellationToken),
            await query.SumAsync(x => x.TotalTokenCount, cancellationToken));
    }

    /// <inheritdoc />
    public StatisticsCapabilitySummary GetCapabilitySummary()
    {
        var mcpTotal = chatPluginManager.McpPlugins.Count;
        var mcpEnabled = chatPluginManager.McpPlugins.AsValueEnumerable().Count(x => x.IsEnabled);

        var builtInFunctions = chatPluginManager.BuiltInPlugins
            .AsValueEnumerable()
            .SelectMany(x => x.GetChatFunctions())
            .Where(x => x.IsVisible)
            .ToList();
        var builtInTotal = builtInFunctions.Count;
        var builtInEnabled = builtInFunctions.Count(x => x.IsEnabled);

        var skills = skillManager.SourceGroups
            .AsValueEnumerable()
            .SelectMany(x => x.Skills)
            .Where(x => x.IsValid)
            .ToList();
        var skillTotal = skills.Count;
        var skillEnabled = skills.Count(x => x.IsEnabled);

        return new StatisticsCapabilitySummary(
            new StatisticsCapabilityGroup(settings.Model.CustomAssistants.Count, settings.Model.CustomAssistants.Count),
            new StatisticsCapabilityGroup(mcpEnabled, mcpTotal),
            new StatisticsCapabilityGroup(builtInEnabled, builtInTotal),
            new StatisticsCapabilityGroup(skillEnabled, skillTotal));
    }

    private static DateOnly ToLocalDate(DateTimeOffset value) => DateOnly.FromDateTime(value.ToLocalTime().Date);

    private static IQueryable<T> ApplyDeviceScope<T>(IQueryable<T> query, int? deviceId) where T : class
    {
        if (deviceId is null) return query;

        return query.Where(x => EF.Property<int>(x, "DeviceId") == deviceId);
    }

    private static async Task<int?> ResolveDeviceIdAsync(
        StatisticsDbContext db,
        StatisticsDeviceScope scope,
        CancellationToken cancellationToken)
    {
        if (scope == StatisticsDeviceScope.AllDevices) return null;

        var deviceGuid = RuntimeConstants.DeviceId;
        return await db.Devices
            .AsNoTracking()
            .Where(x => x.DeviceGuid == deviceGuid)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
