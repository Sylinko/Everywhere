using System.Collections.Specialized;
using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public class ChatPresentationTests
{
    [Test]
    public void RunningTurn_PreservesActivityAndOutputOrder()
    {
        var action = new ActionChatMessage(LucideIconKind.TextSearch, new DirectLocaleKey("Analyze"))
        {
            IsBusy = false,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var assistant = new AssistantChatMessage { IsBusy = true };
        var reasoning = FinishedReasoning("Think");
        var intermediate = FinishedText("Intermediate");
        var function = FunctionMessage("Read", callCount: 2, isBusy: true);

        assistant.AddSpan(reasoning);
        assistant.AddSpan(intermediate);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function));
        using var context = Context(new UserChatMessage("Inspect this", []), action, assistant);
        var presentation = context.Presentation;
        var rows = presentation.Rows.ToList();

        AssertRowTypes<ChatMessagePresentationRow, ChatMessagePresentationRow,
            ReasoningActivityItemPresentationRow, AssistantOutputPresentationRow,
            ActivityGroupPresentationRow, TurnFooterPresentationRow>(rows);

        var reasoningItem = rows.OfType<ReasoningActivityItemPresentationRow>().Single();
        var functionGroup = rows.OfType<ActivityGroupPresentationRow>().Single();
        var functionItem = functionGroup.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(((ChatMessagePresentationRow)rows[1]).Node.Message, Is.SameAs(action));
            Assert.That(functionItem.CallCount, Is.EqualTo(2));
            Assert.That(ChatActivityStatistics.Calculate([functionItem]).ToolCallCount, Is.EqualTo(1));
            Assert.That(functionItem.IsRunning, Is.True);
        });
    }

    [Test]
    public void CompletedTurn_PromotesOnlyTrailingFormalOutput()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        var intermediate = FinishedText("Intermediate");
        var final = FinishedText("Final");
        assistant.AddSpan(FinishedReasoning("Think"));
        assistant.AddSpan(intermediate);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(FunctionMessage("Read", 3, false))
        {
            FinishedAt = DateTimeOffset.UtcNow,
        });
        assistant.AddSpan(final);
        using var context = Context(new UserChatMessage("Do work", []), assistant);
        var presentation = context.Presentation;
        AssertRowTypes<ChatMessagePresentationRow, ReasoningActivityItemPresentationRow,
            AssistantOutputPresentationRow, FunctionCallActivityItemPresentationRow,
            AssistantOutputPresentationRow, TurnFooterPresentationRow>(presentation.Rows);

        var outputs = presentation.Rows.OfType<AssistantOutputPresentationRow>().ToList();
        var finalRow = outputs[^1];
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ProcessSummaryPresentationRow>(), Is.Empty);
            Assert.That(presentation.Rows.OfType<ReasoningActivityItemPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<FunctionCallActivityItemPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(outputs[0].Span, Is.SameAs(intermediate));
            Assert.That(outputs[0].IsFinal, Is.False);
            Assert.That(finalRow.Span, Is.SameAs(final));
            Assert.That(finalRow.IsFinal, Is.True);
        });
    }

    [Test]
    public void CompletedTurn_WithOneActivity_OmitsProcessSummary()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        assistant.AddSpan(FinishedReasoning("Think"));
        assistant.AddSpan(FinishedText("Final"));
        using var context = Context(new UserChatMessage("One activity", []), assistant);
        var presentation = context.Presentation;

        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ProcessSummaryPresentationRow>(), Is.Empty);
            Assert.That(presentation.Rows.OfType<ReasoningActivityItemPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<AssistantOutputPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<TurnFooterPresentationRow>(), Has.Exactly(1).Items);
        });
    }

    [Test]
    public void SourcePropertyRefresh_KeepsVisibleRowInstancesAndListUnchanged()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var function = FunctionMessage("Read", 1, true);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function));
        using var context = Context(new UserChatMessage("Refresh", []), assistant);
        var presentation = context.Presentation;
        var before = presentation.Rows.ToArray();
        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var collectionEvents = 0;
        ((INotifyCollectionChanged)presentation.Rows).CollectionChanged += (_, _) => collectionEvents++;

        function.Content = "A newer preview";

        Assert.Multiple(() =>
        {
            Assert.That(collectionEvents, Is.Zero);
            Assert.That(presentation.Rows, Has.Count.EqualTo(before.Length));
            Assert.That(presentation.Rows.Zip(before).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
            Assert.That(group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single().PreviewText,
                Is.EqualTo("A newer preview"));
        });
    }

    [Test]
    public void ExpandingGroupAndItem_OnlyChangesGroupVisualState()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var firstFunction = FunctionMessage("Read", 1, true);
        firstFunction.DisplaySink.AppendText("preview");
        var secondFunction = FunctionMessage("Read more", 1, true);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan([firstFunction, secondFunction]));
        using var context = Context(new UserChatMessage("Expand", []), assistant);
        var presentation = context.Presentation;
        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var unchangedUserRow = presentation.Rows[0];
        var unchangedRows = presentation.Rows.ToArray();
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)presentation.Rows).CollectionChanged += (_, e) => actions.Add(e.Action);

        group.IsExpanded = true;
        var item = group.Items
            .Single(row => ReferenceEquals(row.Source, firstFunction));
        item.IsExpanded = true;

        Assert.Multiple(() =>
        {
            AssertRowTypes<ChatMessagePresentationRow, ActivityGroupPresentationRow,
                TurnFooterPresentationRow>(presentation.Rows);
            Assert.That(presentation.Rows[0], Is.SameAs(unchangedUserRow));
            Assert.That(presentation.Rows[1], Is.SameAs(group));
            Assert.That(group.Items, Has.Count.EqualTo(2));
            Assert.That(item.IsExpanded, Is.True);
            Assert.That(actions, Is.Empty);
            Assert.That(presentation.Rows.Zip(unchangedRows)
                .All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        });

        group.IsExpanded = false;

        Assert.Multiple(() =>
        {
            Assert.That(group.IsExpanded, Is.False);
            Assert.That(item.IsExpanded, Is.False);
            Assert.That(presentation.Rows.Zip(unchangedRows)
                .All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
        });
    }

    [Test]
    public void RunningSingleActivity_KeepsSameExpandedGroup_WhenSiblingArrives()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var firstFunction = FunctionMessage("Read", 1, true);
        var functionSpan = new AssistantChatMessageFunctionCallSpan(firstFunction);
        assistant.AddSpan(functionSpan);
        using var context = Context(new UserChatMessage("Promote", []), assistant);
        var presentation = context.Presentation;
        var originalGroup = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var firstItem = originalGroup.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();
        var rowsBefore = presentation.Rows.ToArray();

        functionSpan.Add(FunctionMessage("Read more", 1, true));

        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(group, Is.SameAs(originalGroup));
            Assert.That(presentation.Rows, Has.Count.EqualTo(rowsBefore.Length));
            Assert.That(presentation.Rows[1], Is.SameAs(group));
            Assert.That(group.IsExpanded, Is.True);
            Assert.That(group.Items, Has.Count.EqualTo(2));
            Assert.That(group.Items[0], Is.SameAs(firstItem));
        });

        var items = group.Items;
        functionSpan.Add(FunctionMessage("Read final", 1, true));
        Assert.That(group.Items, Is.SameAs(items));
        Assert.That(group.Items, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public void CompletedTool_KeepsOnlyTrailingGroupRunning_WhileAssistantAwaitsContinuation()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var function = FunctionMessage("Read", 1, false);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function)
        {
            FinishedAt = function.FinishedAt,
        });
        using var context = Context(new UserChatMessage("Continue after tool", []), assistant);
        var presentation = context.Presentation;

        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var item = group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.IsRunning, Is.False, "The completed tool must retain its truthful item state.");
            Assert.That(group.IsRunning, Is.True, "The trailing process segment remains open for model continuation.");
            Assert.That(group.IsExpanded, Is.True);
            Assert.That(group.FinishedAt, Is.Null);
        });

        // Formal output closes the preceding process segment even though the overall assistant
        // invocation remains busy while that output is streaming.
        assistant.AddSpan(FinishedText("Continuing"));

        Assert.Multiple(() =>
        {
            Assert.That(group.IsRunning, Is.False);
            Assert.That(group.IsExpanded, Is.True, "The existing 400 ms completion morph still owns collapse timing.");
            Assert.That(group.FinishedAt, Is.Not.Null);
        });
    }

    [Test]
    public void CompletedGroup_LoadedFromHistory_StartsCollapsed()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan([
            FunctionMessage("Read", 1, false),
            FunctionMessage("Read more", 1, false),
            FunctionMessage("Read final", 1, false),
        ]));
        assistant.AddSpan(FinishedText("Done"));
        using var context = Context(new UserChatMessage("History", []), assistant);
        var presentation = context.Presentation;

        var summary = presentation.Rows.OfType<ProcessSummaryPresentationRow>().Single();
        summary.IsExpanded = true;

        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(group.IsExpanded, Is.False);
            Assert.That(group.Statistics.ReasoningCount, Is.Zero);
            Assert.That(group.Statistics.ToolCallCount, Is.EqualTo(3));
            Assert.That(group.Statistics.SubagentCount, Is.Zero);
        });
    }

    [Test]
    public void UpdatingLaterTurn_DoesNotTouchEarlierTurnRows()
    {
        var firstAssistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        firstAssistant.AddSpan(FinishedText("First answer"));
        var secondAssistant = new AssistantChatMessage { IsBusy = true };
        using var context = Context(
            new UserChatMessage("First", []),
            firstAssistant,
            new UserChatMessage("Second", []),
            secondAssistant);
        var presentation = context.Presentation;
        var firstTurnRows = presentation.Rows.Take(3).ToArray();

        secondAssistant.AddSpan(FinishedReasoning("Working"));

        Assert.That(presentation.Rows.Take(3).Zip(firstTurnRows)
            .All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
    }

    [Test]
    public void Continue_DoesNotMergeFailedPartialOutputIntoFinalOutput()
    {
        var failed = new AssistantChatMessage
        {
            IsBusy = false,
            FinishedAt = DateTimeOffset.UtcNow,
            ErrorMessageKey = new DirectLocaleKey("failed"),
        };
        var partial = FinishedText("Partial");
        failed.AddSpan(partial);
        var continued = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        var final = FinishedText("Recovered");
        continued.AddSpan(final);
        using var context = Context(new UserChatMessage("Continue case", []), failed, continued);
        var presentation = context.Presentation;
        var visibleOutput = presentation.Rows.OfType<AssistantOutputPresentationRow>().Single();
        Assert.That(visibleOutput.Span, Is.SameAs(final));

        presentation.Rows.OfType<ProcessSummaryPresentationRow>().Single().IsExpanded = true;
        var outputs = presentation.Rows.OfType<AssistantOutputPresentationRow>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(outputs, Has.Count.EqualTo(2));
            Assert.That(outputs[0].Span, Is.SameAs(partial));
            Assert.That(outputs[1], Is.SameAs(visibleOutput));
        });
    }

    [Test]
    public void SuccessfulEmptyTurn_DisplaysNoResponseRow()
    {
        using var context = Context(
            new UserChatMessage("Empty", []),
            new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow });
        var presentation = context.Presentation;

        Assert.That(presentation.Rows.OfType<NoResponsePresentationRow>(), Has.Exactly(1).Items);
    }

    [Test]
    public void StructuredSubagentBlocks_DriveSubagentCount()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        var function = FunctionMessage("renamed_tool", 1, false);
        function.DisplaySink.AppendBlock(new ChatPluginSubagentDisplayBlock(new ChatContext()));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function) { FinishedAt = DateTimeOffset.UtcNow });
        assistant.AddSpan(FinishedText("Done"));
        using var context = Context(new UserChatMessage("Delegate", []), assistant);
        var presentation = context.Presentation;
        var item = presentation.Rows.OfType<FunctionCallActivityItemPresentationRow>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ProcessSummaryPresentationRow>(), Is.Empty);
            Assert.That(item.CallCount, Is.EqualTo(1));
            Assert.That(ChatActivityStatistics.Calculate([item]).SubagentCount, Is.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public async Task ContextOwnedBusyActivity_MorphsFromRunningGroupToDirectRow_WithoutMutatingSpans()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        using var context = Context(new UserChatMessage("Prepare tools", []), assistant);
        var presentation = context.Presentation;
        using var busyActivity = context.SetBusyActivity(
            LucideIconKind.Server,
            new DirectLocaleKey("Starting MCP"));

        var runningGroup = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var item = runningGroup.Items.OfType<BusyActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(context.Presentation, Is.SameAs(presentation));
            Assert.That(runningGroup.IsRunning, Is.True);
            Assert.That(runningGroup.IsExpanded, Is.True);
            Assert.That(item.IsRunning, Is.True);
            Assert.That(assistant.Spans, Is.Empty);
        });

        busyActivity.Dispose();

        // Completing the item does not complete the trailing Group while the assistant invocation
        // is still waiting for continuation. The item remains truthful and only the Group carries
        // the turn-local continuation state.
        var waitingGroup = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        Assert.That(item.IsRunning, Is.False);
        Assert.That(waitingGroup.IsRunning, Is.True);
        Assert.That(waitingGroup.IsExpanded, Is.True);

        assistant.IsBusy = false;

        // Structural placement intentionally waits while GlowOpacity performs its 320 ms
        // transition. The same Group therefore remains expanded immediately after completion.
        var completedGroup = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        Assert.That(completedGroup.IsRunning, Is.False);
        Assert.That(completedGroup.IsExpanded, Is.True);

        await Task.Delay(500);
        Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>(), Is.Empty);
        Assert.That(presentation.Rows.OfType<BusyActivityItemPresentationRow>(), Has.Exactly(1).Items);
    }

    private static void AssertRowTypes<T1, T2, T3, T4>(IEnumerable<ChatPresentationRow> rows) =>
        Assert.That(rows.Select(row => row.GetType()), Is.EqualTo(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }));

    private static void AssertRowTypes<T1, T2, T3>(IEnumerable<ChatPresentationRow> rows) =>
        Assert.That(rows.Select(row => row.GetType()), Is.EqualTo(new[] { typeof(T1), typeof(T2), typeof(T3) }));

    private static void AssertRowTypes<T1, T2, T3, T4, T5>(IEnumerable<ChatPresentationRow> rows) =>
        Assert.That(rows.Select(row => row.GetType()), Is.EqualTo(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5) }));

    private static void AssertRowTypes<T1, T2, T3, T4, T5, T6>(IEnumerable<ChatPresentationRow> rows) =>
        Assert.That(rows.Select(row => row.GetType()), Is.EqualTo(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6) }));

    private static ChatContext Context(params ChatMessage[] messages)
    {
        var context = new ChatContext();
        foreach (var message in messages) context.Add(message);
        return context;
    }

    private static AssistantChatMessageReasoningSpan FinishedReasoning(string text) =>
        new(text) { FinishedAt = DateTimeOffset.UtcNow };

    private static AssistantChatMessageTextSpan FinishedText(string text) =>
        new(text) { FinishedAt = DateTimeOffset.UtcNow };

    private static FunctionCallChatMessage FunctionMessage(string title, int callCount, bool isBusy)
    {
        var message = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey(title))
        {
            IsBusy = isBusy,
            FinishedAt = DateTimeOffset.UtcNow,
        };

        for (var i = 0; i < callCount; i++)
        {
            message.Calls.Add(new FunctionCallContent(
                functionName: title,
                pluginName: null,
                id: i.ToString(),
                arguments: new KernelArguments()));
        }

        return message;
    }
}
