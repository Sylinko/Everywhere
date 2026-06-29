using Everywhere.Common;
using Everywhere.Statistics;
using Everywhere.Statistics.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Core.Tests.Statistics;

public sealed class StatisticsServiceTests
{
    [Test]
    public async Task GetOverviewAsync_AggregatesRangeAndCurrentDevice_WithProviderTokenSemantics()
    {
        using var database = StatisticsTestDatabase.Create();
        var range = new StatisticsRange(
            new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.FromHours(8)));
        var includedInstant = range.Start.AddHours(1);
        var excludedInstant = range.End;

        await using (var db = await database.Factory.CreateDbContextAsync())
        {
            var currentDevice = await AddDeviceAsync(db, RuntimeConstants.DeviceId);
            var otherDevice = await AddDeviceAsync(db, Guid.CreateVersion7().ToString("D"));
            var currentChatId = Guid.CreateVersion7();

            db.TopicEvents.AddRange(
                CreateTopic(currentDevice.Id, currentChatId, includedInstant),
                CreateTopic(currentDevice.Id, Guid.CreateVersion7(), excludedInstant),
                CreateTopic(otherDevice.Id, Guid.CreateVersion7(), includedInstant));
            db.TurnEvents.AddRange(
                CreateTurn(currentDevice.Id, currentChatId, StatisticsTurnKind.Send, includedInstant),
                CreateTurn(currentDevice.Id, currentChatId, StatisticsTurnKind.Retry, includedInstant.AddMinutes(1)),
                CreateTurn(otherDevice.Id, Guid.CreateVersion7(), StatisticsTurnKind.Send, includedInstant));
            db.ModelInvocationEvents.AddRange(
                CreateModel(currentDevice.Id, currentChatId, includedInstant, input: 100, cached: 25, output: 50, reasoning: 10, total: 150),
                CreateModel(currentDevice.Id, currentChatId, includedInstant.AddMinutes(3), input: 40, cached: 20, output: 10, reasoning: 0, total: 50),
                CreateModel(currentDevice.Id, currentChatId, excludedInstant, input: 999, cached: 999, output: 999, reasoning: 999, total: 999),
                CreateModel(otherDevice.Id, Guid.CreateVersion7(), includedInstant, input: 7, cached: 1, output: 3, reasoning: 0, total: 10));
            db.VisualContextEvents.AddRange(
                CreateVisual(currentDevice.Id, currentChatId, includedInstant, elements: 5, screenshots: 1, images: 2),
                CreateVisual(otherDevice.Id, Guid.CreateVersion7(), includedInstant, elements: 8, screenshots: 0, images: 0));
            db.ToolInvocationEvents.AddRange(
                CreateTool(currentDevice.Id, currentChatId, includedInstant, "mcp.demo"),
                CreateTool(currentDevice.Id, currentChatId, includedInstant.AddMinutes(2), "builtin.files"),
                CreateTool(otherDevice.Id, Guid.CreateVersion7(), includedInstant, "mcp.demo"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(database);
        var currentOverview = await service.GetOverviewAsync(range, StatisticsDeviceScope.CurrentDevice);
        var allOverview = await service.GetOverviewAsync(range);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentOverview.TopicCount, Is.EqualTo(1));
            Assert.That(currentOverview.TurnCount, Is.EqualTo(2));
            Assert.That(currentOverview.TokenSummary.InputTokenCount, Is.EqualTo(140));
            Assert.That(currentOverview.TokenSummary.CachedInputTokenCount, Is.EqualTo(45));
            Assert.That(currentOverview.TokenSummary.OutputTokenCount, Is.EqualTo(60));
            Assert.That(currentOverview.TokenSummary.ReasoningTokenCount, Is.EqualTo(10));
            Assert.That(currentOverview.TokenSummary.TotalTokenCount, Is.EqualTo(200));
            Assert.That(currentOverview.TokenSummary.CachedInputHitRate, Is.EqualTo(45d / 140d).Within(0.0001));
            Assert.That(currentOverview.VisualContextEventCount, Is.EqualTo(1));
            Assert.That(currentOverview.VisualElementCount, Is.EqualTo(5));
            Assert.That(currentOverview.ScreenshotCount, Is.EqualTo(1));
            Assert.That(currentOverview.ImageCount, Is.EqualTo(2));
            Assert.That(currentOverview.ToolInvocationCount, Is.EqualTo(2));

            Assert.That(allOverview.TopicCount, Is.EqualTo(2), "All-devices scope includes both device rows.");
            Assert.That(allOverview.TokenSummary.TotalTokenCount, Is.EqualTo(210));
            Assert.That(allOverview.ToolInvocationCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetHeatmapAsync_GroupsByLocalDateAndUsesMetricSpecificValues()
    {
        using var database = StatisticsTestDatabase.Create();
        var localNow = DateTimeOffset.Now;
        var localTodayAtNoon = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            12,
            0,
            0,
            localNow.Offset);
        var expectedDate = DateOnly.FromDateTime(localTodayAtNoon.Date);

        await using (var db = await database.Factory.CreateDbContextAsync())
        {
            var device = await AddDeviceAsync(db, RuntimeConstants.DeviceId);
            db.ModelInvocationEvents.Add(CreateModel(
                device.Id,
                Guid.CreateVersion7(),
                localTodayAtNoon.ToUniversalTime(),
                input: 4,
                cached: 1,
                output: 8,
                reasoning: 0,
                total: 12));
            db.VisualContextEvents.Add(CreateVisual(
                device.Id,
                Guid.CreateVersion7(),
                localTodayAtNoon.ToUniversalTime(),
                elements: 0,
                screenshots: 0,
                images: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(database);
        var tokenHeatmap = await service.GetHeatmapAsync(StatisticsHeatmapMetric.Tokens, months: 1, StatisticsDeviceScope.CurrentDevice);
        var visualHeatmap = await service.GetHeatmapAsync(StatisticsHeatmapMetric.VisualContext, months: 1, StatisticsDeviceScope.CurrentDevice);

        using (Assert.EnterMultipleScope())
        {
            var tokenDay = tokenHeatmap.Single(x => x.Date == expectedDate);
            Assert.That(tokenDay, Is.TypeOf<StatisticsTokenHeatmapDay>());
            Assert.That(tokenDay.Value, Is.EqualTo(12), "Token heatmap uses input + output tokens for intensity.");
            Assert.That(((StatisticsTokenHeatmapDay)tokenDay).CachedInputTokenCount, Is.EqualTo(1));
            Assert.That(
                visualHeatmap.Single(x => x.Date == expectedDate).Value,
                Is.EqualTo(0),
                "Visual context heatmap uses visual element count only.");
        }
    }

    [Test]
    public async Task StatisticsDatabase_EnforcesOneTopicPerDeviceAndChat()
    {
        using var database = StatisticsTestDatabase.Create();
        await using var db = await database.Factory.CreateDbContextAsync();
        var device = await AddDeviceAsync(db, RuntimeConstants.DeviceId);
        var chatContextId = Guid.CreateVersion7();

        db.TopicEvents.Add(CreateTopic(device.Id, chatContextId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        db.TopicEvents.Add(CreateTopic(device.Id, chatContextId, DateTimeOffset.UtcNow.AddSeconds(1)));

        Assert.That(
            async () => await db.SaveChangesAsync(),
            Throws.TypeOf<DbUpdateException>(),
            "The unique index prevents double-counting a topic on the same device.");
    }

    private static StatisticsService CreateService(StatisticsTestDatabase database) => new(database.Factory);

    private static async Task<DeviceEntity> AddDeviceAsync(StatisticsDbContext db, string deviceGuid)
    {
        var now = DateTimeOffset.UtcNow;
        var device = new DeviceEntity
        {
            DeviceGuid = deviceGuid,
            DisplayName = deviceGuid,
            CreatedAt = now,
            LastSeenAt = now
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static TopicEventEntity CreateTopic(int deviceId, Guid chatContextId, DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            ChatContextId = chatContextId,
            Topic = "Topic",
            CreatedAt = createdAt
        };

    private static TurnEventEntity CreateTurn(
        int deviceId,
        Guid chatContextId,
        StatisticsTurnKind kind,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            ChatContextId = chatContextId,
            UserChatNodeId = Guid.CreateVersion7(),
            Kind = kind,
            CreatedAt = createdAt
        };

    private static ModelInvocationEventEntity CreateModel(
        int deviceId,
        Guid chatContextId,
        DateTimeOffset startedAt,
        long input,
        long cached,
        long output,
        long reasoning,
        long total) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            ChatContextId = chatContextId,
            Purpose = StatisticsModelInvocationPurpose.ChatResponse,
            ModelId = "test-model",
            StartedAt = startedAt,
            FinishedAt = startedAt.AddSeconds(1),
            IsSuccess = true,
            InputTokenCount = input,
            CachedInputTokenCount = cached,
            OutputTokenCount = output,
            ReasoningTokenCount = reasoning,
            TotalTokenCount = total,
            GenerationSeconds = 1
        };

    private static VisualContextEventEntity CreateVisual(
        int deviceId,
        Guid chatContextId,
        DateTimeOffset createdAt,
        int elements,
        int screenshots,
        int images) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            ChatContextId = chatContextId,
            Source = StatisticsVisualContextSource.VisualContextPlugin,
            CreatedAt = createdAt,
            ElementCount = elements,
            ScreenshotCount = screenshots,
            ImageCount = images
        };

    private static ToolInvocationEventEntity CreateTool(
        int deviceId,
        Guid chatContextId,
        DateTimeOffset startedAt,
        string pluginKey) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DeviceId = deviceId,
            ChatContextId = chatContextId,
            PluginKey = pluginKey,
            FunctionName = "read",
            Status = StatisticsToolInvocationStatus.Success,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMilliseconds(10)
        };
}
