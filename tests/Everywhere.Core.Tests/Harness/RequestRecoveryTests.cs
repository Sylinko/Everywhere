using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.Statistics;
using Everywhere.Views;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;

namespace Everywhere.Core.Tests.Harness;

public sealed class RequestRecoveryTests
{
    [AvaloniaTest]
    public async Task StreamConsumer_WhenMetadataArrivesBeforeOutput_TimesEffectiveOutputAndParentsSdkActivities()
    {
        using var parent = new Activity("generation").Start();
        var attempts = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == typeof(ModelSession).FullName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => attempts.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);
        var firstOutputMeasurements = new List<double>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == typeof(ChatService).FullName && instrument.Name == "gen_ai.request.ttft")
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, _, _) => firstOutputMeasurements.Add(value));
        meterListener.Start();
        var recorder = Substitute.For<IStatisticsRecorder>();
        var chatService = CreateService(recorder);
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var message = new AssistantChatMessage { IsBusy = true };
        data.Add(new UserChatMessage("hello", []));
        var assistantNode = data.Add(message);
        var metadataReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdkParents = new List<string?>();
        var client = Substitute.For<Meai.IChatClient>();
        client.GetStreamingResponseAsync(Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream());
        await using var generation = CreateGeneration(new TestSession(client, 0), assistantNode);
        var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException();
        var task = request.Invoke(chatService, [generation, runtime, new List<Meai.ChatMessage>(), message,
            StatisticsModelInvocationPurpose.ChatResponse, CancellationToken.None]) as Task ?? throw new InvalidOperationException();
        await metadataReceived.Task;
        try { Assert.That(firstOutputMeasurements, Is.Empty); }
        finally { allowOutput.TrySetResult(); }
        await task;
        Assert.Multiple(() =>
        {
            Assert.That(firstOutputMeasurements, Has.Count.EqualTo(1));
            Assert.That(attempts, Has.Count.EqualTo(1));
            Assert.That(attempts[0].ParentId, Is.EqualTo(parent.Id));
            Assert.That(sdkParents, Is.EqualTo(new[] { attempts[0].Id, attempts[0].Id }));
            Assert.That(attempts[0].GetTagItem("gen_ai.usage.total_tokens"), Is.EqualTo(12L));
            Assert.That(message.UsageDetails.TotalTokenCount, Is.EqualTo(12));
            Assert.That(message.UsageDetails.TokensPerSecond, Is.Null);
            Assert.That(Activity.Current, Is.SameAs(parent));
        });
        await recorder.Received(1).CompleteModelInvocationAsync(Arg.Any<Guid>(),
            Arg.Is<ChatUsageDetails>(usage => usage.TotalTokenCount == 12), Arg.Any<DateTimeOffset>(), true, false, null, Arg.Any<CancellationToken>());

        async IAsyncEnumerable<Meai.ChatResponseUpdate> Stream()
        {
            await Task.Yield();
            using (var transport = new Activity("transport first read").Start()) sdkParents.Add(transport.ParentId);
            yield return new Meai.ChatResponseUpdate { Role = Meai.ChatRole.Assistant };
            metadataReceived.SetResult();
            await allowOutput.Task;
            using (var transport = new Activity("transport next read").Start()) sdkParents.Add(transport.ParentId);
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, "hello");
            await Task.Delay(150, CancellationToken.None);
            yield return new Meai.ChatResponseUpdate
            {
                Role = Meai.ChatRole.Assistant,
                Contents = [new Meai.UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 })]
            };
        }
    }

    [AvaloniaTest]
    public async Task StreamConsumer_WhenConnectionFailsBeforeOutput_ShowsRetriesAndPreservesCommittedSpans()
    {
        foreach (var hasCommittedOutput in new[] { false, true })
        {
            var recorder = Substitute.For<IStatisticsRecorder>();
            var chatService = CreateService(recorder);
            using var data = new ChatData();
            await using var runtime = new RootChatContext(data, null);
            var message = new AssistantChatMessage { IsBusy = true };
            var committed = new AssistantChatMessageTextSpan("committed");
            if (hasCommittedOutput) message.AddSpan(committed);
            data.Add(new UserChatMessage("hello", []));
            var assistantNode = data.Add(message);
            await runtime.Presentation.PrepareInitialWindowAsync();
            var client = Substitute.For<Meai.IChatClient>();
            var sendCount = 0;
            var retryRows = new List<BusyActivityItemPresentationRow>();
            client.GetStreamingResponseAsync(Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => Attempt(++sendCount));
            await using var generation = CreateGeneration(new TestSession(client, 2), assistantNode);
            var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException();
            var task = request.Invoke(chatService, [generation, runtime, new List<Meai.ChatMessage> { new(Meai.ChatRole.User, "fixed") }, message,
                StatisticsModelInvocationPurpose.ChatResponse, CancellationToken.None]) as Task ?? throw new InvalidOperationException();
            await task;
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(sendCount, Is.EqualTo(3));
                Assert.That(retryRows, Has.Count.EqualTo(2));
                Assert.That(retryRows[1], Is.SameAs(retryRows[0]));
                Assert.That(message.Items.OfType<AssistantChatMessageTextSpan>().Select(span => span.Content),
                    Is.EqualTo(hasCommittedOutput ? new[] { "committed", "recovered" } : new[] { "recovered" }));
                if (hasCommittedOutput) Assert.That(message.Items.First(), Is.SameAs(committed));
                Assert.That(runtime.Presentation.Rows.OfType<ActivityGroupPresentationRow>()
                    .SelectMany(group => group.Items).OfType<BusyActivityItemPresentationRow>(), Is.Empty);
            });
            await recorder.Received(3).CompleteModelInvocationAsync(Arg.Any<Guid>(), Arg.Any<ChatUsageDetails>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

            async IAsyncEnumerable<Meai.ChatResponseUpdate> Attempt(int number)
            {
                await Task.Yield();
                if (number > 1)
                {
                    Dispatcher.UIThread.RunJobs();
                    retryRows.Add(runtime.Presentation.Rows.OfType<ActivityGroupPresentationRow>()
                        .SelectMany(group => group.Items).OfType<BusyActivityItemPresentationRow>().Single());
                    Assert.That(runtime.Presentation.Rows.OfType<PendingAssistantPresentationRow>(), Is.Empty);
                }

                if (number <= 2)
                    throw new HttpRequestException("Proxy connection refused", new System.Net.Sockets.SocketException(10061));
                yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, "recovered");
            }
        }
    }

    [AvaloniaTest]
    public async Task StreamConsumer_WhenPartialAttemptFails_RollsBackOnlyProvisionalOutputAndAccountsEachAttempt()
    {
        var attempts = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == typeof(ModelSession).FullName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => attempts.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var recorder = Substitute.For<IStatisticsRecorder>();
        var chatService = CreateService(recorder);
        using var data = new ChatData();
        await using var runtime = new RootChatContext(data, null);
        var message = new AssistantChatMessage { IsBusy = true };
        message.AddSpan(new AssistantChatMessageTextSpan("committed"));
        var tool = new FunctionCallChatMessage(Lucide.Avalonia.LucideIconKind.Hammer, new DirectLocaleKey("tool"));
        tool.AddCall(new ToolCallRecord("read_file", null, "completed-call", null));
        tool.AddResult(new ToolResultRecord("read_file", null, "completed-call", "previous result"));
        var tools = new AssistantChatMessageFunctionCallSpan();
        tools.Add(tool);
        message.AddSpan(tools);
        message.Metadata = message.Metadata.SetItem("checkpoint", "original");
        data.Add(new UserChatMessage("hello", []));
        var assistantNode = data.Add(message);
        await runtime.Presentation.PrepareInitialWindowAsync();
        var client = Substitute.For<Meai.IChatClient>();
        var sendCount = 0;
        client.GetStreamingResponseAsync(Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Attempt(++sendCount));
        await using var generation = CreateGeneration(new TestSession(client, 1), assistantNode);
        var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException();
        var task = request.Invoke(chatService, [generation, runtime, new List<Meai.ChatMessage> { new(Meai.ChatRole.User, "fixed") }, message,
            StatisticsModelInvocationPurpose.ChatResponse, CancellationToken.None]) as Task ?? throw new InvalidOperationException();
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(sendCount, Is.EqualTo(2));
            Assert.That(message.Items.OfType<AssistantChatMessageTextSpan>().Select(span => span.Content), Is.EqualTo(new[] { "committed", "recovered" }));
            Assert.That(message.Items, Does.Contain(tools));
            Assert.That(tool.Results.Length, Is.EqualTo(1));
            Assert.That(message.Metadata.GetValueOrDefault("checkpoint"), Is.EqualTo("original"));
            Assert.That(runtime.Presentation.Rows.OfType<ActivityGroupPresentationRow>()
                .SelectMany(group => group.Items).Concat(runtime.Presentation.Rows.OfType<ActivityItemPresentationRow>())
                .OfType<BusyActivityItemPresentationRow>(), Is.Empty);
        });
        Assert.Multiple(() =>
        {
            Assert.That(message.UsageDetails.TotalTokenCount, Is.EqualTo(300));
            Assert.That(message.UsageDetails.OutputTokenCount, Is.EqualTo(30));
            Assert.That(message.UsageDetails.SampledOutputTokenCount, Is.EqualTo(20));
            Assert.That(attempts, Has.Count.EqualTo(2));
            Assert.That(attempts[0].Status, Is.EqualTo(ActivityStatusCode.Error));
            Assert.That(attempts[0].GetTagItem("error.type"), Is.EqualTo(typeof(HandledChatException.NetworkError).FullName));
            Assert.That(attempts.Select(activity => activity.GetTagItem("gen_ai.usage.total_tokens")), Is.EqualTo(new object[] { 100L, 200L }));
        });
        await recorder.Received(1).CompleteModelInvocationAsync(Arg.Any<Guid>(), Arg.Is<ChatUsageDetails>(usage => usage.TotalTokenCount == 100),
            Arg.Any<DateTimeOffset>(), false, false, typeof(HandledChatException.NetworkError).FullName, Arg.Any<CancellationToken>());
        await recorder.Received(1).CompleteModelInvocationAsync(Arg.Any<Guid>(), Arg.Is<ChatUsageDetails>(usage => usage.TotalTokenCount == 200),
            Arg.Any<DateTimeOffset>(), true, false, null, Arg.Any<CancellationToken>());
        await recorder.Received(2).StartModelInvocationAsync(Arg.Any<StatisticsModelInvocationDraft>(), Arg.Any<CancellationToken>());
        await recorder.Received(2).CompleteModelInvocationAsync(Arg.Any<Guid>(), Arg.Any<ChatUsageDetails>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        static async IAsyncEnumerable<Meai.ChatResponseUpdate> Attempt(int number)
        {
            await Task.Yield();
            yield return new Meai.ChatResponseUpdate
            {
                Role = Meai.ChatRole.Assistant,
                Contents =
                [
                    new Meai.TextContent(number == 1 ? "discard me" : "re"),
                    new Meai.UsageContent(new UsageDetails { OutputTokenCount = number * 10, TotalTokenCount = number * 100 })
                ]
            };
            if (number == 1) throw new HttpRequestException("broken connection", new System.Net.Sockets.SocketException(10054));
            await Task.Delay(150, CancellationToken.None);
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, "covered");
        }
    }

    [AvaloniaTest]
    public async Task Assistant_WhenCanceledAndReloaded_PreservesStopWithoutErrorOrNoResponse()
    {
        var original = new AssistantChatMessage { IsCanceled = true };
        var restored = MessagePackSerializer.Deserialize<AssistantChatMessage>(MessagePackSerializer.Serialize(original));
        using var data = new ChatData();
        data.Add(new UserChatMessage("hello", []));
        data.Add(restored);
        await using var runtime = new RootChatContext(data, null);
        await runtime.Presentation.PrepareInitialWindowAsync();
        Assert.Multiple(() =>
        {
            Assert.That(restored.IsCanceled, Is.True);
            Assert.That(restored.ErrorMessageKey, Is.Null);
            Assert.That(runtime.Presentation.Rows.OfType<AssistantCanceledPresentationRow>().ToArray(), Has.Length.EqualTo(1));
            Assert.That(runtime.Presentation.Rows.OfType<AssistantErrorPresentationRow>(), Is.Empty);
            Assert.That(runtime.Presentation.Rows.OfType<NoResponsePresentationRow>(), Is.Empty);
        });
    }

    [AvaloniaTest]
    public void Compression_WhenCanceledAndReloaded_RemainsVisibleWithoutSummaryOrError()
    {
        var compression = new ContextCompressionChatMessage(Guid.NewGuid(), "test-model", ContextCompressionTrigger.Manual, null, null);
        compression.Cancel(DateTimeOffset.UtcNow);
        var restored = MessagePackSerializer.Deserialize<ContextCompressionChatMessage>(MessagePackSerializer.Serialize(compression));
        Assert.Multiple(() =>
        {
            Assert.That(restored.IsCanceled, Is.True);
            Assert.That(restored.IsHidden, Is.False);
            Assert.That(restored.HasSummary, Is.False);
            Assert.That(restored.ErrorMessageKey, Is.Null);
            Assert.That(restored.NeedsAutomaticCompaction, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task ContextOperation_WhenParentCancels_StopsChildAndReleasesBusyState()
    {
        using var data = new ChatData();
        await using var context = new RootChatContext(data, null);
        using var parent = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = context.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, parent.Token);
        await entered.Task;
        Assert.That(context.IsBusy, Is.True);
        parent.Cancel();
        try { await task; Assert.Fail("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Assert.That(context.IsBusy, Is.False);
    }

    [AvaloniaTest]
    public async Task StreamRequest_WhenConsumerThrows_DoesNotRetryOrChangeException()
    {
        using var parent = new Activity("generation").Start();
        var attempts = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == typeof(ModelSession).FullName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => attempts.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var cleanupActivity = default(Activity);
        var client = Substitute.For<Meai.IChatClient>();
        var calls = 0;
        client.GetStreamingResponseAsync(Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { calls++; return Stream(); });
        using var session = new TestSession(client, 5);
        var original = new IOException("consumer storage failure");
        try
        {
            await foreach (var update in session.StreamRequestAsync([]))
                if (update is ChatRequestUpdate.Content) throw original;
            Assert.Fail("Expected consumer error");
        }
        catch (IOException error) { Assert.That(error, Is.SameAs(original)); }
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(attempts, Has.Count.EqualTo(1));
            Assert.That(cleanupActivity, Is.SameAs(attempts.Single()));
            Assert.That(Activity.Current, Is.SameAs(parent));
        });

        async IAsyncEnumerable<Meai.ChatResponseUpdate> Stream()
        {
            try
            {
                await Task.Yield();
                yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, "hello");
            }
            finally
            {
                await Task.Yield();
                cleanupActivity = Activity.Current;
            }
        }
    }

    [AvaloniaTest]
    public async Task ContextOperation_WhenChildStops_DoesNotCancelParentToken()
    {
        using var data = new ChatData();
        await using var context = new RootChatContext(data, null);
        using var parent = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = context.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, parent.Token);
        await entered.Task;
        context.Cancel();
        try { await task; Assert.Fail("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Assert.Multiple(() =>
        {
            Assert.That(context.IsBusy, Is.False);
            Assert.That(parent.IsCancellationRequested, Is.False);
        });
    }

    private static GenerationContext CreateGeneration(ModelSession session, ChatMessageNode node) =>
        new(
            new ServiceCollection().BuildServiceProvider(),
            session,
            null,
            Substitute.For<IPromptRenderer>(),
            string.Empty,
            Modalities.Text,
            80,
            -1,
            new GenerationApprovalState(ToolApprovalMode.FullAccess),
            true,
            node);

    private static ChatService CreateService(IStatisticsRecorder recorder)
    {
        var constructor = typeof(ChatService).GetConstructors().Single();
        var args = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(IStatisticsRecorder)
            ? (object)recorder : parameter.ParameterType == typeof(Microsoft.Extensions.Logging.ILogger<ChatService>)
                ? NullLogger<ChatService>.Instance : null).ToArray();
        return (ChatService)constructor.Invoke(args);
    }

    private sealed class TestSession(Meai.IChatClient client, int retries)
        : ModelSession(new AdvancedAssistantConfiguration { ModelId = "test-model" },
            new ModelConnection(ModelProviderSchema.OpenAI, "https://test.invalid", "", new HttpClient(), null, retries))
    {
        public override Meai.IChatClient ChatClient => client;
        public override string Dialect => "openai";
    }
}
