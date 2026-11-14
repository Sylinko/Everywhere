using Everywhere.Core.Tests.Chat;
using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.I18N;
using Everywhere.Statistics;
using Everywhere.Storage;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace Everywhere.Core.Tests.Statistics;

public sealed class StatisticsBackfillerTests
{
    [AvaloniaTest]
    public async Task BackfillAsync_WhenCompressionSeparatesUserAndAssistant_UsesNativeTurnMembership()
    {
        using var chatDatabase = ChatTestDatabase.Create();
        using var statisticsDatabase = StatisticsTestDatabase.Create();
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("question", []));
        data.Add(new ContextCompressionChatMessage(data.RootNode.Id, "test-model", ContextCompressionTrigger.Manual, null, null));
        var assistant = data.Add(new AssistantChatMessage());
        await new ChatTurnStorage(chatDatabase.Factory).SaveDataAsync(data);

        var notificationCenter = new NotificationCenter(new InMemoryKeyValueStorage());
        var backfiller = new StatisticsBackfiller(
            chatDatabase.Factory,
            statisticsDatabase.Factory,
            NullLogger<StatisticsBackfiller>.Instance,
            new NotificationPublisher<StatisticsBackfiller>(notificationCenter));
        await backfiller.BackfillAsync();

        await using var db = await statisticsDatabase.Factory.CreateDbContextAsync();
        var turn = await db.TurnEvents.SingleAsync();
        Assert.That(turn.UserChatNodeId, Is.EqualTo(user.Id));
        Assert.That(turn.AssistantChatNodeId, Is.EqualTo(assistant.Id));
    }

    [AvaloniaTest]
    public async Task BackfillAsync_ImportsHistoricalToolCalls_AndDoesNotDuplicate()
    {
        using var chatDatabase = ChatTestDatabase.Create();
        using var statisticsDatabase = StatisticsTestDatabase.Create();
        var chatId = Guid.CreateVersion7();
        var assistantNodeId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var toolMessage = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("read_file"))
        {
            FinishedAt = now.AddMilliseconds(25)
        };
        var call = new FunctionCallContent("get_file_content_1", null, "call-1");
        toolMessage.AddCall(call);
        toolMessage.AddResult(new FunctionResultContent(call, "ok"));
        var assistantMessage = new AssistantChatMessage
        {
            FinishedAt = now.AddMilliseconds(30)
        };
        assistantMessage.AddSpan(new AssistantChatMessageFunctionCallSpan(toolMessage));

        await using (var chatDb = await chatDatabase.Factory.CreateDbContextAsync())
        {
            chatDb.Chats.Add(new ChatV2Entity
            {
                Id = chatId,
                Topic = "Tool history",
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now
            });
            chatDb.Nodes.Add(new ChatV2NodeEntity
            {
                Id = assistantNodeId,
                ChatContextId = chatId,
                MessageKind = ChatMessageKind.Assistant,
                CreatedAt = now,
                UpdatedAt = now.AddMilliseconds(25),
                Payload = MessagePackSerializer.Serialize<ChatMessage>(assistantMessage)
            });
            await chatDb.SaveChangesAsync();
        }

        var notificationCenter = new NotificationCenter(new InMemoryKeyValueStorage());
        var initializer = new StatisticsBackfiller(
            chatDatabase.Factory,
            statisticsDatabase.Factory,
            NullLogger<StatisticsBackfiller>.Instance,
            new NotificationPublisher<StatisticsBackfiller>(notificationCenter));

        await initializer.BackfillAsync();
        await initializer.BackfillAsync();

        await using var statsDb = await statisticsDatabase.Factory.CreateDbContextAsync();
        var tools = await statsDb.ToolInvocationEvents.AsNoTracking().ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tools, Has.Count.EqualTo(1));
            Assert.That(tools[0].ChatContextId, Is.EqualTo(chatId));
            Assert.That(tools[0].PluginKey, Is.Null);
            Assert.That(tools[0].FunctionName, Is.EqualTo("get_file_content_1"));
            Assert.That(tools[0].Status, Is.EqualTo(StatisticsToolInvocationStatus.Success));
            Assert.That(await statsDb.Metadata.AnyAsync(x => x.Key == "BackfillVersion" && x.Value == "1"), Is.True);
            Assert.That(notificationCenter.Notifications, Has.Count.EqualTo(1));
        }
    }
}