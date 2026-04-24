using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Memory;

public sealed class MemoryManager(
    IDbContextFactory<MemoryDbContext> dbFactory,
    KernelMixinFactory kernelMixinFactory,
    Settings settings,
    ILogger<MemoryManager> logger
) : IMemoryManager, IAsyncInitializer
{
    public string LongTermMemory { get; private set; } = "No memory";

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

    private readonly ActivitySource _activitySource = new(typeof(MemoryManager).FullName.NotNull(), App.Version);
    private readonly Channel<LongTermMemoryUpdateTask> _memoryUpdateChannel = Channel.CreateBounded<LongTermMemoryUpdateTask>(
        new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropNewest
        });

    public async Task InitializeAsync()
    {
        await using var dbContext = await dbFactory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();

        LongTermMemory = dbContext.LongTermMemories.FirstOrDefault(x => x.Id == 1)?.Content ?? "No memory";

        Task.Run(LongTermMemoryUpdateRunner).Detach(logger.ToExceptionHandler());
    }

    public void EnqueueLongTermMemoryUpdateTask(ChatContext chatContext, Assistant assistant, CancellationToken cancellationToken) =>
        // Use chatContext.Items to take a snapshot
        _memoryUpdateChannel.Writer.TryWrite(
            new LongTermMemoryUpdateTask(
                chatContext,
                chatContext.Items
                    .AsValueEnumerable()
                    .Select(n => n.Message)
                    .Where(m => m.Role.Label is "assistant" or "user")
                    .ToList(),
                assistant,
                cancellationToken));

    private async Task LongTermMemoryUpdateRunner()
    {
        await foreach (var task in _memoryUpdateChannel.Reader.ReadAllAsync())
        {
            try
            {
                var cancellationToken = task.CancellationToken;
                if (cancellationToken.IsCancellationRequested) continue;

                var newMemory = await GenerateLongTermMemoryAsync(task);
                if (newMemory.IsNullOrEmpty()) continue;

                LongTermMemory = newMemory;

                await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
                var memoryEntity = await dbContext.LongTermMemories.FirstOrDefaultAsync(x => x.Id == 1, cancellationToken: cancellationToken);
                if (memoryEntity is null)
                {
                    memoryEntity = new LongTermMemoryEntity
                    {
                        Id = 1,
                        Content = newMemory,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    dbContext.LongTermMemories.Add(memoryEntity);
                }
                else
                {
                    memoryEntity.Content = newMemory;
                    memoryEntity.UpdatedAt = DateTimeOffset.UtcNow;
                    dbContext.LongTermMemories.Update(memoryEntity);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update global memory");
            }
        }
    }

    private async ValueTask<string?> GenerateLongTermMemoryAsync(LongTermMemoryUpdateTask task)
    {
        KernelMixin kernelMixin;
        try
        {
            var systemAssistant = settings.SystemAssistant.TitleGeneration.Resolve(task.Assistant);
            kernelMixin = kernelMixinFactory.Create(systemAssistant);
        }
        catch (Exception ex)
        {
            ex = HandledChatException.Handle(ex, null);
            logger.LogError(ex, "Failed to resolve assistant");
            return null;
        }

        using var activity = _activitySource.StartChatActivity("generate_long_term_memory", kernelMixin);

        var lastLongTermMemoryUpdatedAt = task.ChatContext.Metadata.LastLongTermMemoryUpdatedAt;
        var messagesToProcess = task.Messages.AsValueEnumerable()
            .SkipWhile(m => m is not UserChatMessage userChatMessage || userChatMessage.CreatedAt < lastLongTermMemoryUpdatedAt);

        var messageContentsBuilder = new StringBuilder();
        foreach (var message in messagesToProcess)
        {
            switch (message)
            {
                case AssistantChatMessage assistantChatMessage:
                {
                    messageContentsBuilder.AppendLine("<AssistantStart/>");
                    foreach (var span in assistantChatMessage.Items.AsValueEnumerable())
                    {

                    }

                    break;
                }
                case UserStrategyChatMessage userStrategyChatMessage:
                {
                    break;
                }
                case UserChatMessage userChatMessage:
                {
                    messageContentsBuilder.AppendLine("<UserStart/>");
                    messageContentsBuilder.AppendLine(userChatMessage.Content);
                    break;
                }
            }
        }
    }

    private readonly record struct LongTermMemoryUpdateTask(
        ChatContext ChatContext,
        IReadOnlyList<ChatMessage> Messages,
        Assistant Assistant,
        CancellationToken CancellationToken
    );
}