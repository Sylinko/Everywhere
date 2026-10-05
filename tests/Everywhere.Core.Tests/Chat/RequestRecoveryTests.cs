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
        using var context = new ChatContext();
        var message = new AssistantChatMessage { IsBusy = true };
        context.Add(new UserChatMessage("hello", []));
        context.Add(message);
        var metadataReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdkParents = new List<string?>();
        var service = Substitute.For<IChatCompletionService>();
        service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream());
        using var mixin = new TestMixin(service, 0);
        var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException();
        var task = request.Invoke(chatService, [new Kernel(), mixin, context, new ChatHistory(), message,
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
    public async Task BusyActivity_WhenCompressionOwnsIt_AttachesToOperationWithoutAssistantFallback()
    {
        using var context = new ChatContext();
        context.Add(new UserChatMessage("hello", []));
        context.Add(new AssistantChatMessage());
        var compression = new ContextCompressionChatMessage(Guid.Empty, "test", ContextCompressionTrigger.Manual, null, null);
        context.Add(compression);
        var activity = await context.Presentation.SetBusyActivityAsync(Lucide.Avalonia.LucideIconKind.RotateCw, new DirectLocaleKey("retry"), true,
            new DirectLocaleKey("connection failed"), compression);
        var row = context.Presentation.Rows.OfType<BusyActivityItemPresentationRow>().Single();
        Assert.That(row.SecondaryHeaderKey, Is.Not.Null);
        activity.Dispose();
        Assert.That(context.Presentation.Rows.OfType<BusyActivityItemPresentationRow>(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task StreamConsumer_WhenConnectionFailsBeforeOutput_ShowsRetriesAndPreservesCommittedSpans()
    {
        // Exercise both a new response and a later request with already committed output.
        foreach (var hasCommittedOutput in new[] { false, true })
        {
            var recorder = Substitute.For<IStatisticsRecorder>();
            var chatService = CreateService(recorder);
            using var context = new ChatContext();
            var message = new AssistantChatMessage { IsBusy = true };
            var committed = new AssistantChatMessageTextSpan("committed");
            if (hasCommittedOutput) message.AddSpan(committed);
            context.Add(new UserChatMessage("hello", []));
            context.Add(message);
            var service = Substitute.For<IChatCompletionService>();
            var sendCount = 0;
            var retryRows = new List<BusyActivityItemPresentationRow>();
            service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel>(), Arg.Any<CancellationToken>())
                .Returns(_ => Attempt(++sendCount));
            using var mixin = new TestMixin(service, 2);
            var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException();
            var task = request.Invoke(chatService, [new Kernel(), mixin, context, new ChatHistory([new(AuthorRole.User, "fixed")]), message,
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
                Assert.That(context.Presentation.Rows.OfType<ActivityGroupPresentationRow>()
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
                    retryRows.Add(context.Presentation.Rows.OfType<ActivityGroupPresentationRow>()
                        .SelectMany(group => group.Items).OfType<BusyActivityItemPresentationRow>().Single());
                    Assert.That(context.Presentation.Rows.OfType<PendingAssistantPresentationRow>(), Is.Empty);
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
        using var context = new ChatContext();
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
        context.Add(new UserChatMessage("hello", []));
        context.Add(message);
        var service = Substitute.For<IChatCompletionService>();
        var sendCount = 0;
        service.GetStreamingChatMessageContentsAsync(Arg.Any<ChatHistory>(), Arg.Any<PromptExecutionSettings>(), Arg.Any<Kernel>(), Arg.Any<CancellationToken>())
            .Returns(_ => Attempt(++sendCount));
        using var mixin = new TestMixin(service, 1);
        var request = typeof(ChatService).GetMethod("GetStreamingChatMessageContentsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException();
        var task = request.Invoke(chatService, [new Kernel(), mixin, context, new ChatHistory([new(AuthorRole.User, "fixed")]), message,
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
            Assert.That(context.Presentation.Rows.OfType<BusyActivityItemPresentationRow>(), Is.Empty);
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
            if (number == 1) throw new IOException("broken connection");
        }
    }

    [AvaloniaTest]
    public void Assistant_WhenCanceledAndReloaded_PreservesStopWithoutErrorOrNoResponse()
    {
        var original = new AssistantChatMessage { IsCanceled = true };
        var restored = MessagePackSerializer.Deserialize<AssistantChatMessage>(MessagePackSerializer.Serialize(original));
        using var context = new ChatContext();
        context.Add(new UserChatMessage("hello", []));
        context.Add(restored);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(restored.IsCanceled, Is.True);
            Assert.That(restored.ErrorMessageKey, Is.Null);
            Assert.That(context.Presentation.Rows.OfType<AssistantCanceledPresentationRow>().ToArray(), Has.Length.EqualTo(1));
            Assert.That(context.Presentation.Rows.OfType<AssistantErrorPresentationRow>(), Is.Empty);
            Assert.That(context.Presentation.Rows.OfType<NoResponsePresentationRow>(), Is.Empty);
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
        using var context = new ChatContext();
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
        using var context = new ChatContext();
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

    [AvaloniaTest]
    public async Task BusyActivity_WhenUpdatedThenDisposed_PreservesIdentityThenRemovesRow()
    {
        using var context = new ChatContext();
        var message = new AssistantChatMessage { IsBusy = true };
        context.Add(new UserChatMessage("hello", []));
        context.Add(message);
        var activity = await context.Presentation.SetBusyActivityAsync(Lucide.Avalonia.LucideIconKind.RotateCw, new DirectLocaleKey("retry one"),
            true, new DirectLocaleKey("error one"), message);
        var group = context.Presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var row = group.Items.OfType<BusyActivityItemPresentationRow>().Single();
        activity.HeaderKey = new DirectLocaleKey("retry two");
        activity.SecondaryHeaderKey = new DirectLocaleKey("error two");
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(group.Items.Single(), Is.SameAs(row));
            Assert.That(context.Presentation.Rows.OfType<ActivityGroupPresentationRow>().Single(), Is.SameAs(group));
            Assert.That(row.HeaderKey.ToString(), Is.EqualTo("retry two"));
            Assert.That(row.SecondaryHeaderKey?.ToString(), Is.EqualTo("error two"));
            Assert.That(context.Presentation.Rows.OfType<PendingAssistantPresentationRow>(), Is.Empty);
        });
        activity.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.That(context.Presentation.Rows.OfType<ActivityGroupPresentationRow>(), Is.Empty);
    }

    [AvaloniaTest]
    public void Presentation_WhenAttemptSpansAreRemoved_PrunesOutputRowsWithoutReplacingSurvivors()
    {
        using var context = new ChatContext();
        var message = new AssistantChatMessage { IsBusy = true };
        var committed = new AssistantChatMessageTextSpan("committed");
        var provisional = new AssistantChatMessageTextSpan("provisional");
        message.AddSpan(committed);
        message.AddSpan(provisional);
        context.Add(new UserChatMessage("hello", []));
        context.Add(message);
        Dispatcher.UIThread.RunJobs();
        var original = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().First();
        message.Edit(spans => spans.Remove(provisional));
        Dispatcher.UIThread.RunJobs();
        var rows = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToArray();
        var turns = typeof(ChatPresentation).GetField("_turns", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context.Presentation)
            as System.Collections.IDictionary ?? throw new MissingFieldException();
        var turn = turns.Values.Cast<object>().Single();
        var cache = turn.GetType().GetField("_outputRows", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(turn)
            as System.Collections.IDictionary ?? throw new MissingFieldException();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Length.EqualTo(1));
            Assert.That(rows[0], Is.SameAs(original));
            Assert.That(cache.Count, Is.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public void ErrorDetails_WhenRetainedRequestFailuresAreLarge_BoundsFormattingBeforeRendering()
    {
        var failures = Enumerable.Range(1, 10).Select(attempt => new ChatRequestFailure(
            attempt,
            DateTimeOffset.UtcNow,
            (HandledChatException)ChatExceptionNormalizer.Handle(new IOException(new string('x', 10000)), null))).ToArray();
        var error = new ChatRequestException(failures[^1].Exception, failures, failures.Length);
        var view = new ChatErrorDetailsView(error);
        Assert.Multiple(() =>
        {
            Assert.That(view.Details.Length, Is.LessThan(66000));
            Assert.That(view.Details, Does.Contain("truncated"));
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
