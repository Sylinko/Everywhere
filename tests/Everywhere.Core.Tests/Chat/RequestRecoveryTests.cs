using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;
using Everywhere.Common;
using Everywhere.I18N;
using Everywhere.Statistics;
using Everywhere.Views;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public sealed class RequestRecoveryTests
{
    [AvaloniaTest]
    public async Task StreamConsumer_WhenMetadataArrivesBeforeOutput_TimesEffectiveOutputAndParentsSdkActivities()
    {
        using var parent = new Activity("generation").Start();
        var attempts = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == typeof(KernelMixin).FullName,
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
        data.Add(message);
        var metadataReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdkParents = new List<string?>();
        var service = Substitute.For<IChatCompletionService>();
        service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream());
        using var mixin = new TestMixin(service, 0);
        var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException();
        var task = request.Invoke(chatService, [new Kernel(), mixin, runtime, new ChatHistory(), message,
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
            Assert.That(Activity.Current, Is.SameAs(parent));
        });
        await recorder.Received(1).CompleteModelInvocationAsync(Arg.Any<Guid>(),
            Arg.Is<ChatUsageDetails>(usage => usage.TotalTokenCount == 12), Arg.Any<DateTimeOffset>(), true, false, null, Arg.Any<CancellationToken>());

        async IAsyncEnumerable<StreamingChatMessageContent> Stream()
        {
            await Task.Yield();
            using (var transport = new Activity("transport first read").Start()) sdkParents.Add(transport.ParentId);
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, null);
            metadataReceived.SetResult();
            await allowOutput.Task;
            using (var transport = new Activity("transport next read").Start()) sdkParents.Add(transport.ParentId);
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "hello")
                { Metadata = new Dictionary<string, object?> { ["Usage"] = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 } } };
        }
    }

    [AvaloniaTest]
    public async Task StreamConsumer_WhenConnectionFailsBeforeOutput_ShowsRetriesAndPreservesCommittedSpans()
    {
        // Exercise both a new response and a later request with already committed output.
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
            data.Add(message);
            await runtime.Presentation.PrepareInitialWindowAsync();
            var service = Substitute.For<IChatCompletionService>();
            var sendCount = 0;
            var retryRows = new List<BusyActivityItemPresentationRow>();
            service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel>(), Arg.Any<CancellationToken>())
                .Returns(_ => Attempt(++sendCount));
            using var mixin = new TestMixin(service, 2);
            var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException();
            var task = request.Invoke(chatService, [new Kernel(), mixin, runtime, new ChatHistory([new(AuthorRole.User, "fixed")]), message,
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

            async IAsyncEnumerable<StreamingChatMessageContent> Attempt(int number)
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
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, "recovered");
            }
        }
    }

    [AvaloniaTest]
    public async Task StreamConsumer_WhenPartialAttemptFails_RollsBackOnlyProvisionalOutputAndAccountsEachAttempt()
    {
        var attempts = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == typeof(KernelMixin).FullName,
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
        var call = new FunctionCallContent("read_file", id: "completed-call");
        tool.AddCall(call);
        tool.AddResult(new FunctionResultContent(call, "previous result"));
        var tools = new AssistantChatMessageFunctionCallSpan();
        tools.Add(tool);
        message.AddSpan(tools);
        message.Metadata = message.Metadata.SetItem("checkpoint", "original");
        data.Add(new UserChatMessage("hello", []));
        data.Add(message);
        await runtime.Presentation.PrepareInitialWindowAsync();
        var service = Substitute.For<IChatCompletionService>();
        var sendCount = 0;
        service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel>(), Arg.Any<CancellationToken>())
            .Returns(_ => Attempt(++sendCount));
        using var mixin = new TestMixin(service, 1);
        var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException();
        var task = request.Invoke(chatService, [new Kernel(), mixin, runtime, new ChatHistory([new(AuthorRole.User, "fixed")]), message,
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
            Assert.That(runtime.Presentation.Rows.OfType<BusyActivityItemPresentationRow>(), Is.Empty);
        });
        Assert.Multiple(() =>
        {
            Assert.That(message.UsageDetails.TotalTokenCount, Is.EqualTo(300));
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

        static async IAsyncEnumerable<StreamingChatMessageContent> Attempt(int number)
        {
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, number == 1 ? "discard me" : "recovered")
            { Metadata = new Dictionary<string, object?>
                { ["checkpoint"] = number == 1 ? "provisional" : "original", ["Usage"] = new UsageDetails { TotalTokenCount = number * 100 } } };
            if (number == 1) throw new HttpRequestException("broken connection", new System.Net.Sockets.SocketException(10054));
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
            ShouldListenTo = source => source.Name == typeof(KernelMixin).FullName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => attempts.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var cleanupActivity = default(Activity);
        var service = Substitute.For<IChatCompletionService>();
        var calls = 0;
        service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { calls++; return Stream(); });
        using var mixin = new TestMixin(service, 5);
        var original = new IOException("consumer storage failure");
        try
        {
            await foreach (var update in mixin.StreamRequestAsync([], new PromptExecutionSettings()))
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

        async IAsyncEnumerable<StreamingChatMessageContent> Stream()
        {
            try
            {
                await Task.Yield();
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, "hello");
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

    private static ChatService CreateService(IStatisticsRecorder recorder)
    {
        // The normal constructor receives only the collaborators this streaming consumer uses.
        // Unused generation/plugin collaborators stay null; no production test hooks are needed.
        var constructor = typeof(ChatService).GetConstructors().Single();
        var args = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(IStatisticsRecorder)
            ? (object)recorder : parameter.ParameterType == typeof(Microsoft.Extensions.Logging.ILogger<ChatService>)
                ? NullLogger<ChatService>.Instance : null).ToArray();
        return (ChatService)constructor.Invoke(args);
    }

    private sealed class TestMixin(IChatCompletionService service, int retries)
        : KernelMixin(new AdvancedAssistantConfiguration { ModelId = "test-model" },
            new ModelConnection(ModelProviderSchema.OpenAI, "https://test.invalid", "", new HttpClient(), null, retries))
    {
        public override IChatCompletionService ChatCompletionService => service;
        public override bool IsPersistentMessageMetadataKey(string key) => true;
    }
}