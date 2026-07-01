using System.Security.Cryptography;
using System.Text;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Statistics.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ZLinq;

namespace Everywhere.Statistics;

/// <summary>
/// Applies pending migrations for the statistics database during application startup.
/// </summary>
public sealed class StatisticsDbInitializer(
    IDbContextFactory<StatisticsDbContext> dbFactory,
    ILogger<StatisticsDbInitializer> logger
) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

    /// <summary>
    /// Initializes the statistics database schema.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        logger.LogInformation("Statistics database initialized.");
    }
}

/// <summary>
/// Performs the first-run historical import from chat storage into the statistics database.
/// </summary>
/// <remarks>
/// The backfill is intentionally lossy: it can recover topics, turns, stored assistant usage, and historical tool-call counts,
/// but old title-generation and visual context details are only reliable for new events.
/// </remarks>
public sealed class StatisticsBackfiller(
    IDbContextFactory<ChatDbContext> chatDbFactory,
    IDbContextFactory<StatisticsDbContext> statisticsDbFactory,
    ILogger<StatisticsBackfiller> logger,
    INotificationPublisher<StatisticsBackfiller> notificationPublisher
) : IAsyncInitializer
{
    private const string BackfillVersionKey = "BackfillVersion";
    private const string BackfillCompletedAtKey = "BackfillCompletedAt";
    private const string CurrentBackfillVersion = "1";

    public AsyncInitializerIndex Index => (AsyncInitializerIndex)((int)AsyncInitializerIndex.Database + 1);

    /// <summary>
    /// Starts backfill in the background after database initialization.
    /// </summary>
    public Task InitializeAsync()
    {
        Task.Run(BackfillAsync).Detach(logger.ToExceptionHandler());
        return Task.CompletedTask;
    }

    internal async Task BackfillAsync()
    {
        try
        {
            await using var statisticsDb = await statisticsDbFactory.CreateDbContextAsync();
            var version = await statisticsDb.Metadata.AsNoTracking().FirstOrDefaultAsync(x => x.Key == BackfillVersionKey);
            if (version?.Value == CurrentBackfillVersion) return;

            await using var chatDb = await chatDbFactory.CreateDbContextAsync();
            var chats = await chatDb.Chats.AsNoTracking().ToListAsync();
            foreach (var chat in chats)
            {
                await BackfillTopicAsync(statisticsDb, chat);
                await BackfillChatNodesAsync(statisticsDb, chatDb, chat);
            }

            await UpsertMetadataAsync(statisticsDb, BackfillVersionKey, CurrentBackfillVersion);
            await UpsertMetadataAsync(statisticsDb, BackfillCompletedAtKey, DateTimeOffset.UtcNow.ToString("O"));
            await statisticsDb.SaveChangesAsync();
            notificationPublisher.Push(
                "history_backfilled",
                new DynamicLocaleKey(LocaleKey.HomeNotification_StatisticsBackfilled),
                canDismiss: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Statistics backfill failed.");
        }
    }

    private async static Task BackfillTopicAsync(StatisticsDbContext statisticsDb, ChatContextEntity chat)
    {
        var device = await EnsureDeviceAsync(statisticsDb);
        var exists = await statisticsDb.TopicEvents.AnyAsync(x => x.DeviceId == device.Id && x.ChatContextId == chat.Id);
        if (exists) return;

        statisticsDb.TopicEvents.Add(
            new TopicEventEntity
            {
                Id = Guid.CreateVersion7(),
                DeviceId = device.Id,
                ChatContextId = chat.Id,
                Topic = chat.Topic.SafeSubstring(0, 64),
                CreatedAt = chat.CreatedAt
            });
    }

    private async Task BackfillChatNodesAsync(StatisticsDbContext statisticsDb, ChatDbContext chatDb, ChatContextEntity chat)
    {
        var device = await EnsureDeviceAsync(statisticsDb);
        var rows = await chatDb.Nodes.AsNoTracking().Where(x => x.ChatContextId == chat.Id).ToListAsync();
        var rowsById = rows.AsValueEnumerable().ToDictionary(x => x.Id);
        var toolMessages = new List<(int SpanIndex, int MessageIndex, FunctionCallChatMessage Message)>();

        foreach (var assistantRow in rows.Where(x => x.Author == "assistant"))
        {
            AssistantChatMessage? assistantMessage;
            try
            {
                assistantMessage = MessagePackSerializer.Deserialize<ChatMessage>(assistantRow.Payload) as AssistantChatMessage;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to deserialize assistant node {NodeId} during statistics backfill.", assistantRow.Id);
                continue;
            }

            if (assistantMessage is null) continue;

            Guid? turnId = null;
            if (assistantRow.ParentId is { } parentId && rowsById.TryGetValue(parentId, out var parentRow) && parentRow.Author == "user")
            {
                turnId = Guid.CreateVersion7();
                statisticsDb.TurnEvents.Add(
                    new TurnEventEntity
                    {
                        Id = turnId.Value,
                        DeviceId = device.Id,
                        ChatContextId = chat.Id,
                        UserChatNodeId = parentRow.Id,
                        AssistantChatNodeId = assistantRow.Id,
                        Kind = StatisticsTurnKind.Send,
                        CreatedAt = assistantRow.CreatedAt
                    });
            }

            var usageDetails = assistantMessage.UsageDetails;
            if (usageDetails is { TotalTokenCount: > 0 } or { InputTokenCount: > 0 } or { OutputTokenCount: > 0 })
            {
                statisticsDb.ModelInvocationEvents.Add(
                    new ModelInvocationEventEntity
                    {
                        Id = Guid.CreateVersion7(),
                        DeviceId = device.Id,
                        TurnEventId = turnId,
                        ChatContextId = chat.Id,
                        AssistantChatNodeId = assistantRow.Id,
                        Purpose = StatisticsModelInvocationPurpose.Backfill,
                        StartedAt = assistantMessage.CreatedAt,
                        FinishedAt = assistantMessage.FinishedAt == default ? assistantRow.UpdatedAt : assistantMessage.FinishedAt,
                        IsSuccess = assistantMessage.ErrorMessageKey is null,
                        InputTokenCount = usageDetails.InputTokenCount,
                        CachedInputTokenCount = usageDetails.CachedInputTokenCount,
                        OutputTokenCount = usageDetails.OutputTokenCount,
                        ReasoningTokenCount = usageDetails.ReasoningTokenCount,
                        TotalTokenCount = usageDetails.TotalTokenCount,
                        GenerationSeconds = usageDetails.TotalGenerationSeconds
                    });
            }

            toolMessages.Clear();
            assistantMessage.Edit(spans =>
            {
                for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
                {
                    if (spans[spanIndex] is not AssistantChatMessageFunctionCallSpan span) continue;

                    span.Edit(functionCalls =>
                    {
                        toolMessages.AddRange(functionCalls.Select((t, i) => (spanIndex, messageIndex: i, t)));
                    });
                }
            });

            foreach (var (spanIndex, messageIndex, toolMessage) in toolMessages)
            {
                for (var index = 0; index < toolMessage.Calls.Count; index++)
                {
                    var call = toolMessage.Calls[index];
                    var id = CreateBackfillToolInvocationId(assistantRow.Id, spanIndex, messageIndex, index, call.Id);
                    if (await statisticsDb.ToolInvocationEvents.AnyAsync(x => x.Id == id)) continue;

                    var result = toolMessage.Results.FirstOrDefault(x => x.CallId == call.Id);
                    var isError = IsBackfilledToolError(toolMessage, result);
                    statisticsDb.ToolInvocationEvents.Add(
                        new ToolInvocationEventEntity
                        {
                            Id = id,
                            DeviceId = device.Id,
                            ChatContextId = chat.Id,
                            FunctionName = call.FunctionName.SafeSubstring(0, 255),
                            Status = isError ?
                                StatisticsToolInvocationStatus.Error :
                                StatisticsToolInvocationStatus.Success,
                            StartedAt = toolMessage.CreatedAt == default ? assistantRow.CreatedAt : toolMessage.CreatedAt,
                            FinishedAt = toolMessage.FinishedAt == default ? assistantRow.UpdatedAt : toolMessage.FinishedAt
                        });
                }
            }
        }

        static bool IsBackfilledToolError(FunctionCallChatMessage message, FunctionResultContent? result)
        {
            return message.ErrorMessageKey is not null || result?.InnerContent is Exception;
        }
    }

    private static Guid CreateBackfillToolInvocationId(
        Guid assistantRowId,
        int spanIndex,
        int messageIndex,
        int callIndex,
        string? callId)
    {
        var input = $"{assistantRowId:N}:{spanIndex}:{messageIndex}:{callIndex}:{callId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]);
    }

    private static async Task<DeviceEntity> EnsureDeviceAsync(StatisticsDbContext statisticsDb)
    {
        var deviceGuid = RuntimeConstants.DeviceId.SafeSubstring(0, 36);
        var device = await statisticsDb.Devices.FirstOrDefaultAsync(x => x.DeviceGuid == deviceGuid);
        if (device is not null) return device;

        var now = DateTimeOffset.UtcNow;
        device = new DeviceEntity
        {
            DeviceGuid = deviceGuid,
            DisplayName = Environment.MachineName.SafeSubstring(0, 128),
            CreatedAt = now,
            LastSeenAt = now
        };
        statisticsDb.Devices.Add(device);
        await statisticsDb.SaveChangesAsync();
        return device;
    }

    private static async Task UpsertMetadataAsync(StatisticsDbContext statisticsDb, string key, string value)
    {
        var entity = await statisticsDb.Metadata.FirstOrDefaultAsync(x => x.Key == key);
        if (entity is null)
        {
            statisticsDb.Metadata.Add(
                new StatisticsMetadataEntity
                {
                    Key = key.SafeSubstring(0, 64),
                    Value = value.SafeSubstring(0, 2048),
                    UpdatedAt = DateTimeOffset.UtcNow
                });
        }
        else
        {
            entity.Value = value.SafeSubstring(0, 2048);
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
