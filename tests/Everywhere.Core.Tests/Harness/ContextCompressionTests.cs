using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Core.I18N;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.Statistics;
using Everywhere.Views;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ContextUsageState = Everywhere.Harness.ContextUsageState;
using MEAI = Microsoft.Extensions.AI;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;

namespace Everywhere.Core.Tests.Harness;

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

        var contents = history.Select(message => message.Text).ToArray();
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

        var contents = history.Select(message => message.Text).ToArray();
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

        var contents = history.Select(message => message.Text).ToArray();
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
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);

        await using (await runtime.BeginContextCompactionAsync())
        {
            Assert.That(runtime.ContextCompaction.IsRunning, Is.True);
        }

        Assert.That(runtime.ContextCompaction.IsRunning, Is.False);
    }

    [Test]
    public async Task ResolvePendingAutomaticCompressionTrigger_WhenAutomaticAttemptFailed_RetriesOnNextOperation()
    {
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var failure = CreateRunningCompression(ContextCompressionTrigger.ContextLengthRecovery);
        failure.Fail(new DirectLocaleKey("failed"), DateTimeOffset.UtcNow);
        data.Add(failure);

        var trigger = await ResolvePendingAutomaticCompressionTrigger(runtime);

        Assert.That(trigger, Is.EqualTo(ContextCompressionTrigger.ContextLengthRecovery));
    }

    [Test]
    public async Task ResolvePendingAutomaticCompressionTrigger_WhenManualAttemptFailed_UsesUsageThresholdOnly()
    {
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var failure = CreateRunningCompression(ContextCompressionTrigger.Manual);
        failure.Fail(new DirectLocaleKey("failed"), DateTimeOffset.UtcNow);
        data.Add(failure);

        Assert.That(await ResolvePendingAutomaticCompressionTrigger(runtime), Is.Null);

        runtime.ContextUsage.Report(
            CreateUsage(160_000),
            "test-model",
            200_000);

        Assert.That(
            await ResolvePendingAutomaticCompressionTrigger(runtime),
            Is.EqualTo(ContextCompressionTrigger.Automatic));

        Assert.That(await ResolvePendingAutomaticCompressionTrigger(runtime, 90), Is.Null);
        Assert.That(
            await ResolvePendingAutomaticCompressionTrigger(runtime, 75),
            Is.EqualTo(ContextCompressionTrigger.Automatic));
    }

    [Test]
    public void ResolveCompressionBoundary_WhenBeforeCurrentUser_LeavesNewUserInSuffix()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("old request", []));
        var oldAssistant = Assistant("old response");
        data.Add(oldAssistant);
        var userNode = data.Add(new UserChatMessage("new request", []));
        var pendingAssistant = new AssistantChatMessage { IsBusy = true };
        data.Add(pendingAssistant);

        var boundary = ResolveCompressionBoundary(data, CompressionBoundary.Before(userNode));
        var oldAssistantNode = data.SelectedNodes.Single(node => ReferenceEquals(node.Message, oldAssistant));

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
    public async Task RequestCompressionSummary_WhenPrefixActiveAndFirstBatchCallsParallelTools_ReusesPrefixRetriesOnceAndSucceeds()
    {
        var recordedMessages = new List<IReadOnlyList<MEAI.ChatMessage>>();
        var recordedOptions = new List<MEAI.ChatOptions?>();
        var attempt = 0;

        var client = Substitute.For<MEAI.IChatClient>();
        client.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<MEAI.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recordedMessages.Add(call.ArgAt<IEnumerable<MEAI.ChatMessage>>(0).ToArray());
                recordedOptions.Add(call.ArgAt<MEAI.ChatOptions?>(1));
                attempt++;
                return attempt == 1
                    ? StreamUpdates(
                        new MEAI.ChatResponseUpdate
                        {
                            Role = MEAI.ChatRole.Assistant,
                            Contents =
                            [
                                new MEAI.FunctionCallContent("call-1", "alpha_tool"),
                                new MEAI.FunctionCallContent("call-2", "beta_tool")
                            ]
                        })
                    : StreamUpdates(
                        new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "Consolidated summary."));
            });

        var chatService = CreateChatService();
        using var data = new ChatData();
        var userNode = data.Add(new UserChatMessage("First request", []));
        var assistantNode = data.Add(Assistant("First reply"));
        var compressionNode = data.Add(CreateRunningCompression(ContextCompressionTrigger.Automatic));
        await using var runtime = new RootChatContext(data, null);

        var windowStartNodeId = userNode.Id;
        var toolSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");
        var lockedTools = new[]
        {
            MEAI.AIFunctionFactory.CreateDeclaration("alpha_tool", "Alpha", toolSchema),
            MEAI.AIFunctionFactory.CreateDeclaration("beta_tool", "Beta", toolSchema)
        };
        var identity = new ContextPrefixIdentity("openai", "gpt-5", true, null, windowStartNodeId);
        runtime.PrefixState.Apply(
            [
                new MEAI.ChatMessage(MEAI.ChatRole.System, "Locked initial system prompt"),
                new MEAI.ChatMessage(MEAI.ChatRole.User, "First request")
            ],
            lockedTools,
            identity,
            DateTimeOffset.UtcNow);

        using var session = new TestCompressionSession(client, "openai", "gpt-5", supportsToolCall: true);
        await using var generation = CreateGeneration(session, "Updated mid-turn system prompt", assistantNode);

        var summary = await InvokeRequestCompressionSummary(
            chatService,
            runtime,
            generation,
            [userNode.Message, assistantNode.Message],
            null,
            windowStartNodeId,
            compressionNode,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Is.EqualTo("Consolidated summary."));
            Assert.That(recordedMessages, Has.Count.EqualTo(2));

            Assert.That(recordedOptions[0]?.ToolMode, Is.EqualTo(MEAI.ChatToolMode.None));
            Assert.That(recordedOptions[0]?.Tools?.Select(static t => t.Name), Is.EqualTo(new[] { "alpha_tool", "beta_tool" }));
            Assert.That(recordedMessages[0][0].Text, Is.EqualTo("Locked initial system prompt"));
            Assert.That(recordedMessages[0][^1].Text, Is.EqualTo(DefaultPrompts.ContextCompressionPrompt));

            // Second attempt keeps the exact same prefix and appends the batch calls plus rejection tool results.
            Assert.That(recordedOptions[1]?.ToolMode, Is.EqualTo(MEAI.ChatToolMode.None));
            Assert.That(recordedOptions[1]?.Tools?.Select(static t => t.Name), Is.EqualTo(new[] { "alpha_tool", "beta_tool" }));
            Assert.That(recordedMessages[1][0].Text, Is.EqualTo("Locked initial system prompt"));
            Assert.That(recordedMessages[1][^2].Contents.OfType<MEAI.FunctionCallContent>().Select(static c => c.CallId),
                Is.EqualTo(new[] { "call-1", "call-2" }));
            Assert.That(recordedMessages[1][^1].Role, Is.EqualTo(MEAI.ChatRole.Tool));
            Assert.That(recordedMessages[1][^1].Contents.OfType<MEAI.FunctionResultContent>().Select(static r => r.CallId),
                Is.EqualTo(new[] { "call-1", "call-2" }));
        });
    }

    [AvaloniaTest]
    public async Task RequestCompressionSummary_WhenSecondBatchStillCallsTools_ThrowsToolCallNotAllowed()
    {
        var attempts = 0;
        var client = Substitute.For<MEAI.IChatClient>();
        client.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<MEAI.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                return StreamUpdates(
                    new MEAI.ChatResponseUpdate
                    {
                        Role = MEAI.ChatRole.Assistant,
                        Contents = [new MEAI.FunctionCallContent($"call-{attempts}", "alpha_tool")]
                    });
            });

        var chatService = CreateChatService();
        using var data = new ChatData();
        var userNode = data.Add(new UserChatMessage("First request", []));
        var assistantNode = data.Add(Assistant("First reply"));
        var compressionNode = data.Add(CreateRunningCompression(ContextCompressionTrigger.Automatic));
        await using var runtime = new RootChatContext(data, null);

        using var session = new TestCompressionSession(client, "openai", "gpt-5", supportsToolCall: true);
        await using var generation = CreateGeneration(session, "System prompt", assistantNode);

        var ex = await Assert.CatchAsync<Exception>(async () =>
            await InvokeRequestCompressionSummary(
                chatService,
                runtime,
                generation,
                [userNode.Message, assistantNode.Message],
                null,
                userNode.Id,
                compressionNode,
                CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.GetType().Name, Is.EqualTo("ContextCompressionOutputException"));
            Assert.That(
                ex.GetType().GetProperty("LocaleKey")?.GetValue(ex),
                Is.EqualTo(LocaleKey.ContextCompression_Error_ToolCallNotAllowed));
            Assert.That(attempts, Is.EqualTo(2));
        });
    }

    [AvaloniaTest]
    public async Task RequestCompressionSummary_WhenSourceHeadDivergesFromEpoch_DropsLockedTools()
    {
        var recordedOptions = new List<MEAI.ChatOptions?>();
        var client = Substitute.For<MEAI.IChatClient>();
        client.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<MEAI.ChatMessage>>(),
                Arg.Any<MEAI.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recordedOptions.Add(call.ArgAt<MEAI.ChatOptions?>(1));
                return StreamUpdates(new MEAI.ChatResponseUpdate(MEAI.ChatRole.Assistant, "Trimmed summary."));
            });

        var chatService = CreateChatService();
        using var data = new ChatData();
        var trimmedUserNode = data.Add(new UserChatMessage("Oldest request", []));
        var survivingUserNode = data.Add(new UserChatMessage("Surviving request", []));
        var survivingAssistantNode = data.Add(Assistant("Surviving reply"));
        var compressionNode = data.Add(CreateRunningCompression(ContextCompressionTrigger.Automatic));
        await using var runtime = new RootChatContext(data, null);

        var toolSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");
        var lockedTools = new[] { MEAI.AIFunctionFactory.CreateDeclaration("alpha_tool", "Alpha", toolSchema) };
        runtime.PrefixState.Apply(
            [
                new MEAI.ChatMessage(MEAI.ChatRole.System, "Locked prompt"),
                new MEAI.ChatMessage(MEAI.ChatRole.User, "Oldest request")
            ],
            lockedTools,
            new ContextPrefixIdentity("openai", "gpt-5", true, null, trimmedUserNode.Id),
            DateTimeOffset.UtcNow);

        using var session = new TestCompressionSession(client, "openai", "gpt-5", supportsToolCall: true);
        await using var generation = CreateGeneration(session, "Fresh prompt", survivingAssistantNode);

        // Simulate source history trimmed past Oldest request -> head diverges, so PrefixState returns null.
        var summary = await InvokeRequestCompressionSummary(
            chatService,
            runtime,
            generation,
            [survivingUserNode.Message, survivingAssistantNode.Message],
            null,
            trimmedUserNode.Id,
            compressionNode,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(summary, Is.EqualTo("Trimmed summary."));
            Assert.That(recordedOptions, Has.Count.EqualTo(1));
            Assert.That(recordedOptions[0]?.Tools, Is.Null);
            Assert.That(recordedOptions[0]?.ToolMode, Is.EqualTo(MEAI.ChatToolMode.None));
        });
    }

    [AvaloniaTest]
    public async Task Presentation_WhenCompressionChangesState_KeepsTerminalRowsAndHidesRestartOrphan()
    {
        using var data = new ChatData();
        var running = CreateRunningCompression(ContextCompressionTrigger.Manual);
        var serialized = MessagePackSerializer.Serialize<ChatMessage>(running);
        var restartOrphan = MessagePackSerializer.Deserialize<ChatMessage>(serialized);

        data.Add(running);
        running.Complete("summary", false, DateTimeOffset.UtcNow);
        data.Add(restartOrphan);
        await using var runtime = new RootChatContext(data, null);
        await runtime.Presentation.PrepareInitialWindowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(data.SelectedNodes.Select(node => node.Message).ToArray(), Has.Length.EqualTo(3));
            Assert.That(runtime.Presentation.Rows.OfType<ChatMessagePresentationRow>().Select(row => row.Node.Message), Is.EqualTo(new[] { running }));
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
            new MEAI.ChatResponseUpdate
            {
                Role = MEAI.ChatRole.Assistant,
                Contents =
                [
                    new MEAI.UsageContent(new UsageDetails
                    {
                        InputTokenCount = totalTokenCount,
                        OutputTokenCount = 0,
                        TotalTokenCount = totalTokenCount
                    })
                ]
            });
        return usage;
    }

    private static Task<ContextCompressionTrigger?> ResolvePendingAutomaticCompressionTrigger(
        ChatContext data,
        int contextCompressionThreshold = ContextUsageSnapshot.DefaultCompressionThresholdPercentage) =>
        InvokeResolvePendingAutomaticCompressionTrigger(
            null,
            data,
            contextCompressionThreshold,
            CancellationToken.None);

    private static Guid ResolveCompressionBoundary(ChatData data, CompressionBoundary boundary)
    {
        var nodes = data.SelectedNodes;
        var boundaryIndex = InvokeResolveCompressionBoundary(null, nodes, boundary);
        return boundaryIndex >= 0 ? nodes[boundaryIndex].Id : Guid.Empty;
    }

    private static bool TryTrimOldestConversationUnit(List<ChatMessage> messages)
    {
        // Match the local function without depending on the compiler's numeric name suffix.
        var method = typeof(ChatService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name.StartsWith("<CompactContextAsync>g__TryTrimOldestConversationUnit|", StringComparison.Ordinal));
        return method.Invoke(null, [messages]) is true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ResolvePendingAutomaticCompressionTriggerAsync")]
    private static extern Task<ContextCompressionTrigger?> InvokeResolvePendingAutomaticCompressionTrigger(
        ChatService? klass,
        ChatContext data,
        int contextCompressionThreshold,
        CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ResolveCompressionBoundary")]
    private static extern int InvokeResolveCompressionBoundary(
        ChatService? klass,
        IReadOnlyList<ChatMessageNode> nodes,
        CompressionBoundary boundary);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RequestCompressionSummaryAsync")]
    private static extern Task<string> InvokeRequestCompressionSummary(
        ChatService instance,
        ChatContext chatContext,
        GenerationContext generationContext,
        IReadOnlyList<ChatMessage> messages,
        Guid? activeSummaryNodeId,
        Guid? windowStartNodeId,
        ChatMessageNode compressionNode,
        CancellationToken cancellationToken);

    private static ChatService CreateChatService()
    {
        var constructor = typeof(ChatService).GetConstructors().Single();
        var args = constructor.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var t when t == typeof(IStatisticsRecorder) => (object)Substitute.For<IStatisticsRecorder>(),
            var t when t == typeof(Microsoft.Extensions.Logging.ILogger<ChatService>) => NullLogger<ChatService>.Instance,
            _ => null
        }).ToArray();
        return (ChatService)constructor.Invoke(args);
    }

    private static GenerationContext CreateGeneration(ModelSession session, string systemPrompt, ChatMessageNode node) =>
        new(
            new ServiceCollection().BuildServiceProvider(),
            session,
            null,
            Substitute.For<IPromptRenderer>(),
            systemPrompt,
            Modalities.Text,
            80,
            -1,
            new GenerationApprovalState(ToolApprovalMode.FullAccess),
            false,
            node);

    private static async IAsyncEnumerable<MEAI.ChatResponseUpdate> StreamUpdates(params MEAI.ChatResponseUpdate[] updates)
    {
        await Task.Yield();
        foreach (var update in updates)
        {
            yield return update;
        }
    }

    private sealed class TestCompressionSession(
        MEAI.IChatClient client,
        string dialect,
        string modelId,
        bool supportsToolCall)
        : ModelSession(
            new AdvancedAssistantConfiguration
            {
                ModelId = modelId,
                SupportsToolCall = supportsToolCall,
                InputModalities = Modalities.Text
            },
            new ModelConnection(ModelProviderSchema.OpenAI, "https://test.invalid", "", new HttpClient(), null, 0))
    {
        public override MEAI.IChatClient ChatClient => client;
        public override string Dialect => dialect;
    }
}