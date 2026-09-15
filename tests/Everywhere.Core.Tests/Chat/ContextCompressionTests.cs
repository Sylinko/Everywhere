using System.Runtime.CompilerServices;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using MessagePack;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;

namespace Everywhere.Core.Tests.Chat;

public sealed class ContextCompressionTests
{
    [Test]
    public async Task BuildHistory_WhenCompressionRowFollowsNewTurn_AnchorsSummaryBeforeRawSuffix()
    {
        var obsoleteUserNode = new ChatMessageNode(new UserChatMessage("obsolete request", []));
        var anchorNode = new ChatMessageNode(Assistant("obsolete response"));
        var currentUserNode = new ChatMessageNode(new UserChatMessage("current request", []));
        var currentAssistantNode = new ChatMessageNode(Assistant("current response"));
        var compression = CreateCompletedCompression("preserved decision", anchorNode.Id);
        ChatMessageNode[] nodes =
        [
            obsoleteUserNode,
            anchorNode,
            currentUserNode,
            currentAssistantNode,
            new ChatMessageNode(compression)
        ];

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system prompt",
            nodes,
            -1,
            Modalities.Text,
            0);

        var contents = history.Select(message => message.Content).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(contents, Has.None.Contains("obsolete request"));
            Assert.That(contents, Has.None.Contains("obsolete response"));
            Assert.That(contents, Has.Some.Contains("preserved decision"));
            Assert.That(contents, Has.Some.EqualTo("current request"));
            Assert.That(contents, Has.Some.EqualTo("current response"));
            Assert.That(
                Array.FindIndex(contents, content => content?.Contains("preserved decision") is true),
                Is.LessThan(Array.IndexOf(contents, "current request")));
        });
    }

    [Test]
    public async Task BuildHistory_WhenCompressionAnchorIsMissing_IgnoresCompressionSummary()
    {
        ChatMessageNode[] nodes =
        [
            new(new UserChatMessage("original request", [])),
            new(Assistant("original response")),
            new(CreateCompletedCompression("invalid summary", Guid.CreateVersion7()))
        ];

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system prompt",
            nodes,
            -1,
            Modalities.Text,
            200_000);

        var contents = history.Select(message => message.Content).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(contents, Has.Some.EqualTo("original request"));
            Assert.That(contents, Has.Some.EqualTo("original response"));
            Assert.That(contents, Has.None.Contains("invalid summary"));
        });
    }

    [Test]
    public async Task BuildHistory_WhenRoundsAreLimited_AlwaysRetainsCompressionSummary()
    {
        var anchorNode = new ChatMessageNode(Assistant("covered response"));
        ChatMessageNode[] nodes =
        [
            new(new UserChatMessage("covered request", [])),
            anchorNode,
            new(CreateCompletedCompression("compressed history", anchorNode.Id)),
            new(new UserChatMessage("older suffix request", [])),
            new(Assistant("older suffix response")),
            new(new UserChatMessage("latest request", [])),
            new(Assistant("latest response"))
        ];

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system prompt",
            nodes,
            0,
            Modalities.Text,
            0);

        var contents = history.Select(message => message.Content).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(contents, Has.Some.Contains("compressed history"));
            Assert.That(contents, Has.None.EqualTo("older suffix request"));
            Assert.That(contents, Has.None.EqualTo("older suffix response"));
            Assert.That(contents, Has.Some.EqualTo("latest request"));
            Assert.That(contents, Has.Some.EqualTo("latest response"));
        });
    }

    [Test]
    public void SelectContextMessages_WhenContextLimitIsKnown_RetainsRecentUserTextWithoutAttachments()
    {
        var firstUser = new UserChatMessage("first", []);
        var latestUser = new UserChatMessage(
            "latest",
            [new TextAttachment(new DirectLocaleKey("attachment"), "attachment payload")]);
        var anchorNode = new ChatMessageNode(Assistant("covered response"));
        ChatMessageNode[] nodes =
        [
            new(firstUser),
            new(Assistant("first response")),
            new(latestUser),
            anchorNode,
            new(CreateCompletedCompression("summary", anchorNode.Id))
        ];

        var selected = ChatHistoryBuilder.SelectContextMessages(nodes, -1, 200_000);

        Assert.Multiple(() =>
        {
            Assert.That(selected.OfType<UserChatMessage>().Select(message => message.Content), Is.EqualTo(new[] { "first", "latest" }));
            Assert.That(selected.OfType<UserChatMessage>().All(message => message.Attachments.Count == 0), Is.True);
            Assert.That(selected, Has.Some.SameAs(nodes[^1].Message));
        });
    }

    [Test]
    public void MessagePackRoundTrip_WhenMessageIsCompression_PreservesMetadata()
    {
        ChatMessage message = CreateCompletedCompression("summary", Guid.CreateVersion7(), wasSourceHistoryTrimmed: true);

        var bytes = MessagePackSerializer.Serialize(message);
        var restored = MessagePackSerializer.Deserialize<ChatMessage>(bytes);

        Assert.That(restored, Is.TypeOf<ContextCompressionChatMessage>());
        var compression = (ContextCompressionChatMessage)restored;
        Assert.Multiple(() =>
        {
            Assert.That(compression.Summary, Is.EqualTo("summary"));
            Assert.That(compression.SourceModelId, Is.EqualTo("test-model"));
            Assert.That(compression.ReportedTotalTokensBefore, Is.EqualTo(160_000));
            Assert.That(compression.DeclaredContextLimitBefore, Is.EqualTo(200_000));
            Assert.That(compression.Trigger, Is.EqualTo(ContextCompressionTrigger.Manual));
            Assert.That(compression.WasSourceHistoryTrimmed, Is.True);
            Assert.That(compression.FinishedAt, Is.Not.Null);
        });
    }

    [Test]
    public void CompressionState_WhenAttemptFinishes_DerivesVisibilityAndAutomaticRetry()
    {
        var automaticFailure = CreateRunningCompression(ContextCompressionTrigger.Automatic);
        automaticFailure.Fail(new DynamicLocaleKey(LocaleKey.ContextCompression_Error_EmptyResponse), DateTimeOffset.UtcNow);
        var manualFailure = CreateRunningCompression(ContextCompressionTrigger.Manual);
        manualFailure.Fail(new DynamicLocaleKey(LocaleKey.ContextCompression_Error_EmptyResponse), DateTimeOffset.UtcNow);
        var success = CreateRunningCompression(ContextCompressionTrigger.Automatic);
        success.Complete("summary", false, DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(automaticFailure.IsHidden, Is.False);
            Assert.That(automaticFailure.NeedsAutomaticCompaction, Is.True);
            Assert.That(manualFailure.IsHidden, Is.False);
            Assert.That(manualFailure.NeedsAutomaticCompaction, Is.False);
            Assert.That(success.IsHidden, Is.False);
            Assert.That(success.NeedsAutomaticCompaction, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task BeginContextCompactionAsync_WhenScopeIsDisposed_RestoresIdleState()
    {
        using var context = new ChatContext();

        await using (await context.BeginContextCompactionAsync())
        {
            Assert.That(context.ContextCompaction.IsRunning, Is.True);
        }

        Assert.That(context.ContextCompaction.IsRunning, Is.False);
    }

    [Test]
    public void ResolvePendingAutomaticCompressionTrigger_WhenAutomaticAttemptFailed_RetriesOnNextOperation()
    {
        using var context = new ChatContext();
        var failure = CreateRunningCompression(ContextCompressionTrigger.ContextLengthRecovery);
        failure.Fail(new DirectLocaleKey("failed"), DateTimeOffset.UtcNow);
        context.Add(failure);

        var trigger = ResolvePendingAutomaticCompressionTrigger(context);

        Assert.That(trigger, Is.EqualTo(ContextCompressionTrigger.ContextLengthRecovery));
    }

    [Test]
    public void ResolvePendingAutomaticCompressionTrigger_WhenManualAttemptFailed_UsesUsageThresholdOnly()
    {
        using var context = new ChatContext();
        var failure = CreateRunningCompression(ContextCompressionTrigger.Manual);
        failure.Fail(new DirectLocaleKey("failed"), DateTimeOffset.UtcNow);
        context.Add(failure);

        Assert.That(ResolvePendingAutomaticCompressionTrigger(context), Is.Null);

        context.ContextUsage.Report(
            CreateUsage(160_000),
            "test-model",
            200_000);

        Assert.That(
            ResolvePendingAutomaticCompressionTrigger(context),
            Is.EqualTo(ContextCompressionTrigger.Automatic));

        Assert.That(ResolvePendingAutomaticCompressionTrigger(context, 90), Is.Null);
        Assert.That(
            ResolvePendingAutomaticCompressionTrigger(context, 75),
            Is.EqualTo(ContextCompressionTrigger.Automatic));
    }

    [Test]
    public void ResolveCompressionBoundary_WhenNewUserPrecedesEmptyAssistant_LeavesNewUserInSuffix()
    {
        using var context = new ChatContext();
        context.Add(new UserChatMessage("old request", []));
        var oldAssistant = Assistant("old response");
        context.Add(oldAssistant);
        context.Add(new UserChatMessage("new request", []));
        var pendingAssistant = new AssistantChatMessage { IsBusy = true };
        context.Add(pendingAssistant);

        var boundary = ResolveCompressionBoundary(context, pendingAssistant);
        var oldAssistantNode = context.Items.Single(node => ReferenceEquals(node.Message, oldAssistant));

        Assert.That(boundary, Is.EqualTo(oldAssistantNode.Id));
    }

    [Test]
    public void TrimOldestConversationUnit_WhenPreviousSummaryExists_DropsOptionalPrefixBeforeSummary()
    {
        List<ChatMessage> messages =
        [
            new UserChatMessage("retained first", []),
            new UserChatMessage("retained second", []),
            CreateCompletedCompression("summary", Guid.CreateVersion7()),
            new UserChatMessage("latest", []),
            Assistant("latest response")
        ];

        Assert.That(TryTrimOldestConversationUnit(messages), Is.True);
        Assert.That(messages.OfType<UserChatMessage>().Select(message => message.Content), Is.EqualTo(new[] { "retained second", "latest" }));

        Assert.That(TryTrimOldestConversationUnit(messages), Is.True);
        Assert.That(messages.OfType<UserChatMessage>().Select(message => message.Content), Is.EqualTo(new[] { "latest" }));

        Assert.That(TryTrimOldestConversationUnit(messages), Is.True);
        Assert.That(messages, Has.None.TypeOf<ContextCompressionChatMessage>());

        Assert.That(TryTrimOldestConversationUnit(messages), Is.False);
    }

    [Test]
    public void Snapshot_WhenUsageAndLimitAreKnown_ComputesRatioAndAutomaticCompactionState()
    {
        var snapshot = new ContextUsageSnapshot(
            ContextUsageKind.ProviderReported,
            ContextUsageUnavailableReason.None,
            160_000,
            50_000,
            4_000,
            1_000,
            160_000,
            200_000,
            "test-model",
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.HasUsageRatio, Is.True);
            Assert.That(snapshot.UsageRatio, Is.EqualTo(0.8d));
            Assert.That(snapshot.UsagePercentage, Is.EqualTo(80));
            Assert.That(snapshot.HasReachedCompressionThreshold(80), Is.True);
        });
    }

    [Test]
    public void Snapshot_WhenCompressionThresholdChanges_UsesNewPolicyWithoutInvalidatingMeasurement()
    {
        var snapshot = new ContextUsageSnapshot(
            ContextUsageKind.ProviderReported,
            ContextUsageUnavailableReason.None,
            160_000,
            50_000,
            4_000,
            1_000,
            160_000,
            200_000,
            "test-model",
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.HasReachedCompressionThreshold(90), Is.False);
            Assert.That(snapshot.Kind, Is.EqualTo(ContextUsageKind.ProviderReported));
        });

        Assert.That(snapshot.HasReachedCompressionThreshold(75), Is.True);

        var state = new ContextUsageState();
        state.Report(CreateUsage(160_000), "test-model", 200_000);
        state.UpdateModel("test-model", 200_000);
        Assert.That(state.Snapshot.Kind, Is.EqualTo(ContextUsageKind.ProviderReported));
    }

    [Test]
    public void Report_WhenProviderReportsZeroTokens_DoesNotTreatMeasurementAsUnavailable()
    {
        var usage = CreateUsage(0);
        var state = new ContextUsageState();

        state.Report(usage, "test-model", 200_000);

        Assert.Multiple(() =>
        {
            Assert.That(usage.HasUsage, Is.True);
            Assert.That(state.Snapshot.Kind, Is.EqualTo(ContextUsageKind.ProviderReported));
            Assert.That(state.Snapshot.HasUsage, Is.True);
            Assert.That(state.Snapshot.TotalTokenCount, Is.Zero);
        });

        state.UpdateModel("another-model", 100_000);

        Assert.That(state.Snapshot.Kind, Is.EqualTo(ContextUsageKind.Estimated));
    }

    [AvaloniaTest]
    public void DisplayItems_WhenCompressionChangesState_KeepsTerminalRowsAndHidesRestartOrphan()
    {
        using var context = new ChatContext();
        var running = CreateRunningCompression(ContextCompressionTrigger.Manual);
        var serialized = MessagePackSerializer.Serialize<ChatMessage>(running);
        var restartOrphan = MessagePackSerializer.Deserialize<ChatMessage>(serialized);

        context.Add(running);
        running.Complete("summary", false, DateTimeOffset.UtcNow);
        context.Add(restartOrphan);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(context.Items.Select(node => node.Message).ToArray(), Has.Length.EqualTo(3));
            Assert.That(context.DisplayItems.Select(node => node.Message), Is.EqualTo(new[] { running }));
            Assert.That(restartOrphan.IsHidden, Is.True);
        });
    }

    private static ContextCompressionChatMessage CreateRunningCompression(ContextCompressionTrigger trigger) =>
        new(
            Guid.CreateVersion7(),
            "test-model",
            trigger,
            160_000,
            200_000);

    private static ContextCompressionChatMessage CreateCompletedCompression(
        string summary,
        Guid coveredThroughNodeId,
        bool wasSourceHistoryTrimmed = false)
    {
        var message = new ContextCompressionChatMessage(
            coveredThroughNodeId,
            "test-model",
            ContextCompressionTrigger.Manual,
            160_000,
            200_000);
        message.Complete(summary, wasSourceHistoryTrimmed, DateTimeOffset.UtcNow);
        return message;
    }

    private static AssistantChatMessage Assistant(string content)
    {
        var message = new AssistantChatMessage();
        message.AddSpan(new AssistantChatMessageTextSpan(content));
        return message;
    }

    private static ChatUsageDetails CreateUsage(long totalTokenCount)
    {
        var usage = new ChatUsageDetails();
        usage.Update(
            new ChatMessageContent(
                AuthorRole.Assistant,
                string.Empty,
                metadata: new Dictionary<string, object?>
                {
                    ["Usage"] = new UsageDetails
                    {
                        InputTokenCount = totalTokenCount,
                        OutputTokenCount = 0,
                        TotalTokenCount = totalTokenCount
                    }
                }));
        return usage;
    }

    private static ContextCompressionTrigger? ResolvePendingAutomaticCompressionTrigger(
        ChatContext context,
        int contextCompressionThreshold = ContextUsageSnapshot.DefaultCompressionThresholdPercentage) =>
        InvokeResolvePendingAutomaticCompressionTrigger(
            null,
            context,
            contextCompressionThreshold);

    private static Guid ResolveCompressionBoundary(ChatContext context, AssistantChatMessage assistantMessage) =>
        InvokeResolveCompressionBoundary(null, context, assistantMessage);

    private static bool TryTrimOldestConversationUnit(List<ChatMessage> messages) =>
        InvokeTryTrimOldestConversationUnit(null, messages);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ResolvePendingAutomaticCompressionTrigger")]
    private static extern ContextCompressionTrigger? InvokeResolvePendingAutomaticCompressionTrigger(
        ChatService? klass,
        ChatContext context,
        int contextCompressionThreshold);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ResolveCompressionBoundary")]
    private static extern Guid InvokeResolveCompressionBoundary(
        ChatService? klass,
        ChatContext context,
        AssistantChatMessage? assistantMessage);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "TryTrimOldestConversationUnit")]
    private static extern bool InvokeTryTrimOldestConversationUnit(
        ChatService? klass,
        List<ChatMessage> messages);
}
