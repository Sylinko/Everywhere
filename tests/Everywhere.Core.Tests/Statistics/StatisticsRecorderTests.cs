using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.Statistics;
using Everywhere.Statistics.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Statistics;

public sealed class StatisticsRecorderTests
{
    [Test]
    public async Task RecordTurn_CreatesOneTopicAndSeparateTurns_ForUserRequests()
    {
        using var database = StatisticsTestDatabase.Create();
        var settings = new Settings();
        var recorder = new StatisticsRecorder(
            database.Factory,
            settings,
            NullLogger<StatisticsRecorder>.Instance);
        var chatContext = new ChatContext();
        chatContext.Metadata.Topic = "Statistics plan";
        var userMessage = new UserChatMessage("please implement this", []);
        chatContext.Add(userMessage);
        var userNode = chatContext.Items.Last(x => ReferenceEquals(x.Message, userMessage));

        var sendTurnId = await recorder.RecordTurnAsync(chatContext, userNode, StatisticsTurnKind.Send);
        var editTurnId = await recorder.RecordTurnAsync(chatContext, userNode, StatisticsTurnKind.Edit);

        await using var db = await database.Factory.CreateDbContextAsync();
        var topics = await db.TopicEvents.AsNoTracking().ToListAsync();
        var turns = await db.TurnEvents.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sendTurnId, Is.Not.Null);
            Assert.That(editTurnId, Is.Not.Null);
            Assert.That(topics, Has.Count.EqualTo(1), "A topic is counted once even when multiple turns happen in it.");
            Assert.That(topics[0].ChatContextId, Is.EqualTo(chatContext.Metadata.Id));
            Assert.That(topics[0].CreatedAt, Is.EqualTo(chatContext.Metadata.DateCreated));
            Assert.That(turns, Has.Count.EqualTo(2));
            Assert.That(turns.Select(x => x.Kind), Is.EqualTo([StatisticsTurnKind.Send, StatisticsTurnKind.Edit]));
            Assert.That(turns.All(x => x.UserChatNodeId == userNode.Id), Is.True);
        }
    }

    [Test]
    public async Task ModelToolAndVisualEvents_RecordCompletionStateAndUsage()
    {
        using var database = StatisticsTestDatabase.Create();
        var recorder = new StatisticsRecorder(
            database.Factory,
            new Settings(),
            NullLogger<StatisticsRecorder>.Instance);
        var chatContextId = Guid.CreateVersion7();
        var turnEventId = Guid.CreateVersion7();
        var modelInvocationId = Guid.CreateVersion7();
        var toolInvocationId = Guid.CreateVersion7();

        await recorder.StartModelInvocationAsync(new StatisticsModelInvocationDraft(
            modelInvocationId,
            turnEventId,
            chatContextId,
            Guid.CreateVersion7(),
            StatisticsModelInvocationPurpose.ChatResponse,
            new string('m', 300),
            DateTimeOffset.UtcNow));
        await recorder.CompleteModelInvocationAsync(
            modelInvocationId,
            new ChatUsageDetails
            {
                InputTokenCount = 100,
                CachedInputTokenCount = 40,
                OutputTokenCount = 25,
                ReasoningTokenCount = 5,
                TotalTokenCount = 125,
                TotalGenerationSeconds = 2.5
            },
            DateTimeOffset.UtcNow.AddSeconds(3),
            isSuccess: false,
            isCanceled: true,
            errorType: typeof(OperationCanceledException).FullName);
        await recorder.StartToolInvocationAsync(new StatisticsToolInvocationDraft(
            toolInvocationId,
            turnEventId,
            modelInvocationId,
            chatContextId,
            new string('p', 300),
            new string('f', 300),
            DateTimeOffset.UtcNow));
        await recorder.CompleteToolInvocationAsync(
            toolInvocationId,
            StatisticsToolInvocationStatus.Denied,
            DateTimeOffset.UtcNow.AddMilliseconds(50));
        await recorder.RecordVisualContextAsync(new StatisticsVisualContextDraft(
            turnEventId,
            chatContextId,
            StatisticsVisualContextSource.ScreenCapture,
            ElementCount: 3,
            ScreenshotCount: 1,
            ImageCount: 1,
            ByteCount: 4096));

        await using var db = await database.Factory.CreateDbContextAsync();
        var model = await db.ModelInvocationEvents.AsNoTracking().SingleAsync();
        var tool = await db.ToolInvocationEvents.AsNoTracking().SingleAsync();
        var visual = await db.VisualContextEvents.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.TurnEventId, Is.EqualTo(turnEventId));
            Assert.That(model.ChatContextId, Is.EqualTo(chatContextId));
            Assert.That(model.ModelId, Has.Length.EqualTo(255));
            Assert.That(model.IsSuccess, Is.False);
            Assert.That(model.IsCanceled, Is.True);
            Assert.That(model.InputTokenCount, Is.EqualTo(100));
            Assert.That(model.CachedInputTokenCount, Is.EqualTo(40));
            Assert.That(model.OutputTokenCount, Is.EqualTo(25));
            Assert.That(model.GenerationSeconds, Is.EqualTo(2.5));
            Assert.That(tool.ModelInvocationEventId, Is.EqualTo(modelInvocationId));
            Assert.That(tool.PluginKey, Has.Length.EqualTo(255));
            Assert.That(tool.FunctionName, Has.Length.EqualTo(255));
            Assert.That(tool.Status, Is.EqualTo(StatisticsToolInvocationStatus.Denied));
            Assert.That(visual.Source, Is.EqualTo(StatisticsVisualContextSource.ScreenCapture));
            Assert.That(visual.ByteCount, Is.EqualTo(4096));
        }
    }

    [Test]
    public async Task RecordingMethods_DoNotWriteNewRows_WhenStatisticsAreDisabled()
    {
        using var database = StatisticsTestDatabase.Create();
        var settings = new Settings
        {
            Common =
            {
                IsStatisticsEnabled = false
            }
        };
        var recorder = new StatisticsRecorder(
            database.Factory,
            settings,
            NullLogger<StatisticsRecorder>.Instance);
        var chatContext = new ChatContext();
        chatContext.Add(new UserChatMessage("do not count me", []));

        var topicId = await recorder.RecordTopicAsync(chatContext);
        var turnId = await recorder.RecordTurnAsync(chatContext, chatContext.Items.Last(), StatisticsTurnKind.Send);
        await recorder.StartModelInvocationAsync(new StatisticsModelInvocationDraft(
            Guid.CreateVersion7(),
            null,
            chatContext.Metadata.Id,
            null,
            StatisticsModelInvocationPurpose.ChatResponse,
            "test-model",
            DateTimeOffset.UtcNow));
        await recorder.RecordVisualContextAsync(new StatisticsVisualContextDraft(
            null,
            chatContext.Metadata.Id,
            StatisticsVisualContextSource.ImageAttachment,
            ImageCount: 1,
            ByteCount: 128));

        await using var db = await database.Factory.CreateDbContextAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(topicId, Is.Null);
            Assert.That(turnId, Is.Null);
            Assert.That(await db.Devices.CountAsync(), Is.Zero);
            Assert.That(await db.TopicEvents.CountAsync(), Is.Zero);
            Assert.That(await db.TurnEvents.CountAsync(), Is.Zero);
            Assert.That(await db.ModelInvocationEvents.CountAsync(), Is.Zero);
            Assert.That(await db.VisualContextEvents.CountAsync(), Is.Zero);
        }
    }
}
