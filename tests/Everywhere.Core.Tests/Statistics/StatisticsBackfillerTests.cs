using System.Data.Common;
using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.I18N;
using Everywhere.Statistics;
using Everywhere.Statistics.Database;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using NSubstitute;

namespace Everywhere.Core.Tests.Statistics;

public sealed class StatisticsBackfillerTests
{
    [Test]
    public async Task Backfill_WhenCanceledDuringQuery_DoesNotPublishCompletion()
    {
        using var database = StatisticsTestDatabase.Create();
        await using var connectionContext = await database.Factory.CreateDbContextAsync();
        var interceptor = new PendingQueryInterceptor();
        var options = new DbContextOptionsBuilder<StatisticsDbContext>()
            .UseSqlite(connectionContext.Database.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;
        var notifications = Substitute.For<INotificationPublisher<StatisticsBackfiller>>();
        var backfiller = new StatisticsBackfiller(
            Substitute.For<IDbContextFactory<ChatDbContext>>(),
            new StatisticsDbContextFactory(options),
            NullLogger<StatisticsBackfiller>.Instance,
            notifications);
        using var lifetime = new CancellationTokenSource();

        var work = backfiller.BackfillAsync(lifetime.Token);
        try
        {
            await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.Cancel();
            await work.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(await connectionContext.Metadata.AnyAsync(x => x.Key == "BackfillVersion"), Is.False);
            Assert.That(notifications.ReceivedCalls(), Is.Empty);
        }
        finally
        {
            interceptor.Release.TrySetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(5));
        }
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
            chatDb.Chats.Add(new ChatContextEntity
            {
                Id = chatId,
                Topic = "Tool history",
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now
            });
            chatDb.Nodes.Add(new ChatNodeEntity
            {
                Id = assistantNodeId,
                ChatContextId = chatId,
                Author = "assistant",
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

        await initializer.BackfillAsync(CancellationToken.None);
        await initializer.BackfillAsync(CancellationToken.None);

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

    private sealed class PendingQueryInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
