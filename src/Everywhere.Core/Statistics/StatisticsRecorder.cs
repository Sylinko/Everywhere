using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.Statistics.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Statistics;

/// <summary>
/// Persists real-time statistics events into the local statistics database.
/// </summary>
/// <remarks>
/// This recorder is intentionally best-effort. It catches and logs failures so usage collection cannot break normal chat flow.
/// </remarks>
public sealed class StatisticsRecorder(
    IDbContextFactory<StatisticsDbContext> dbFactory,
    Settings settings,
    ILogger<StatisticsRecorder> logger
) : IStatisticsRecorder
{
    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private int? _deviceId;

    /// <inheritdoc />
    public Task<Guid?> RecordTopicAsync(ChatContext chatContext, CancellationToken cancellationToken = default) =>
        SafeAsync<Guid?>(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return null;

                var deviceId = await EnsureDeviceAsync(token);
                await using var db = await dbFactory.CreateDbContextAsync(token);
                var exists = await db.TopicEvents.AnyAsync(
                    x => x.DeviceId == deviceId && x.ChatContextId == chatContext.Metadata.Id,
                    token);
                if (exists) return null;

                var id = Guid.CreateVersion7();
                db.TopicEvents.Add(
                    new TopicEventEntity
                    {
                        Id = id,
                        DeviceId = deviceId,
                        ChatContextId = chatContext.Metadata.Id,
                        Topic = chatContext.Metadata.Topic.SafeSubstring(0, 64),
                        CreatedAt = chatContext.Metadata.DateCreated
                    });
                await db.SaveChangesAsync(token);
                return id;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Guid?> RecordTurnAsync(
        ChatContext chatContext,
        ChatMessageNode? userNode,
        StatisticsTurnKind kind,
        CancellationToken cancellationToken = default) =>
        SafeAsync<Guid?>(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return null;

                await RecordTopicAsync(chatContext, token);
                var deviceId = await EnsureDeviceAsync(token);
                var id = Guid.CreateVersion7();
                await using var db = await dbFactory.CreateDbContextAsync(token);
                db.TurnEvents.Add(
                    new TurnEventEntity
                    {
                        Id = id,
                        DeviceId = deviceId,
                        ChatContextId = chatContext.Metadata.Id,
                        UserChatNodeId = userNode?.Id,
                        Kind = kind,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                await db.SaveChangesAsync(token);
                return id;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task RecordVisualContextAsync(StatisticsVisualContextDraft draft, CancellationToken cancellationToken = default) =>
        SafeAsync(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return;

                var deviceId = await EnsureDeviceAsync(token);
                await using var db = await dbFactory.CreateDbContextAsync(token);
                db.VisualContextEvents.Add(
                    new VisualContextEventEntity
                    {
                        Id = Guid.CreateVersion7(),
                        DeviceId = deviceId,
                        TurnEventId = draft.TurnEventId,
                        ChatContextId = draft.ChatContextId,
                        Source = draft.Source,
                        CreatedAt = DateTimeOffset.UtcNow,
                        ElementCount = draft.ElementCount,
                        ScreenshotCount = draft.ScreenshotCount,
                        ImageCount = draft.ImageCount,
                        ByteCount = draft.ByteCount
                    });
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task StartModelInvocationAsync(StatisticsModelInvocationDraft draft, CancellationToken cancellationToken = default) =>
        SafeAsync(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return;

                var deviceId = await EnsureDeviceAsync(token);
                await using var db = await dbFactory.CreateDbContextAsync(token);
                db.ModelInvocationEvents.Add(
                    new ModelInvocationEventEntity
                    {
                        Id = draft.Id,
                        DeviceId = deviceId,
                        TurnEventId = draft.TurnEventId,
                        ChatContextId = draft.ChatContextId,
                        AssistantChatNodeId = draft.AssistantChatNodeId,
                        Purpose = draft.Purpose,
                        ModelId = draft.ModelId.SafeSubstring(0, 255),
                        StartedAt = draft.StartedAt
                    });
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task CompleteModelInvocationAsync(
        Guid invocationId,
        ChatUsageDetails usageDetails,
        DateTimeOffset finishedAt,
        bool isSuccess,
        bool isCanceled,
        string? errorType,
        CancellationToken cancellationToken = default) =>
        SafeAsync(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return;

                await using var db = await dbFactory.CreateDbContextAsync(token);
                var entity = await db.ModelInvocationEvents.FirstOrDefaultAsync(x => x.Id == invocationId, token);
                if (entity is null) return;

                entity.FinishedAt = finishedAt;
                entity.IsSuccess = isSuccess;
                entity.IsCanceled = isCanceled;
                entity.ErrorType = errorType.SafeSubstring(0, 255);
                entity.InputTokenCount = usageDetails.InputTokenCount;
                entity.CachedInputTokenCount = usageDetails.CachedInputTokenCount;
                entity.OutputTokenCount = usageDetails.OutputTokenCount;
                entity.ReasoningTokenCount = usageDetails.ReasoningTokenCount;
                entity.TotalTokenCount = usageDetails.TotalTokenCount;
                entity.GenerationSeconds = usageDetails.TotalGenerationSeconds;
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task StartToolInvocationAsync(StatisticsToolInvocationDraft draft, CancellationToken cancellationToken = default) =>
        SafeAsync(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return;

                var deviceId = await EnsureDeviceAsync(token);
                await using var db = await dbFactory.CreateDbContextAsync(token);
                db.ToolInvocationEvents.Add(
                    new ToolInvocationEventEntity
                    {
                        Id = draft.Id,
                        DeviceId = deviceId,
                        TurnEventId = draft.TurnEventId,
                        ModelInvocationEventId = draft.ModelInvocationEventId,
                        ChatContextId = draft.ChatContextId,
                        PluginKey = draft.PluginKey.SafeSubstring(0, 255),
                        FunctionName = draft.FunctionName.SafeSubstring(0, 255),
                        Status = StatisticsToolInvocationStatus.Error,
                        StartedAt = draft.StartedAt
                    });
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task CompleteToolInvocationAsync(
        Guid invocationId,
        StatisticsToolInvocationStatus status,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default) =>
        SafeAsync(
            async token =>
            {
                if (!settings.Common.IsStatisticsEnabled) return;

                await using var db = await dbFactory.CreateDbContextAsync(token);
                var entity = await db.ToolInvocationEvents.FirstOrDefaultAsync(x => x.Id == invocationId, token);
                if (entity is null) return;

                entity.Status = status;
                entity.FinishedAt = finishedAt;
                await db.SaveChangesAsync(token);
            },
            cancellationToken);

    private async Task<int> EnsureDeviceAsync(CancellationToken cancellationToken)
    {
        if (_deviceId is { } cached) return cached;

        await _deviceLock.WaitAsync(cancellationToken);
        try
        {
            if (_deviceId is { } cachedInsideLock) return cachedInsideLock;

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var deviceGuid = RuntimeConstants.DeviceId.SafeSubstring(0, 36);
            var device = await db.Devices.FirstOrDefaultAsync(x => x.DeviceGuid == deviceGuid, cancellationToken);
            if (device is null)
            {
                device = new DeviceEntity
                {
                    DeviceGuid = deviceGuid,
                    DisplayName = Environment.MachineName.SafeSubstring(0, 128),
                    CreatedAt = now,
                    LastSeenAt = now
                };
                db.Devices.Add(device);
            }
            else
            {
                device.LastSeenAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            _deviceId = device.Id;
            return device.Id;
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    private async Task SafeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record statistics.");
        }
    }

    private async Task<T?> SafeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record statistics.");
            return default;
        }
    }
}
