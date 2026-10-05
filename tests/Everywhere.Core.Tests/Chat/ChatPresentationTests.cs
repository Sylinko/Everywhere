using System.Collections.Specialized;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Markdig;
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
            ReasoningActivityItemPresentationRow, AssistantTextOutputPresentationRow,
            ActivityGroupPresentationRow, TurnFooterPresentationRow>(rows);

        var functionGroup = rows.OfType<ActivityGroupPresentationRow>().Single();
        var functionItem = functionGroup.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(((ChatMessagePresentationRow)rows[1]).Node.Message, Is.SameAs(action));
            Assert.That(functionItem.FunctionCall.Calls.Length, Is.EqualTo(2));
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
        AssertRowTypes<ChatMessagePresentationRow, ProcessSummaryPresentationRow,
            AssistantTextOutputPresentationRow, TurnFooterPresentationRow>(presentation.Rows);
        presentation.Rows.OfType<ProcessSummaryPresentationRow>().Single().IsExpanded = true;

        var outputs = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToList();
        var finalRow = outputs[^1];
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ProcessSummaryPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<ReasoningActivityItemPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<FunctionCallActivityItemPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(outputs[0].TextSpan, Is.SameAs(intermediate));
            Assert.That(outputs[0].IsFinal, Is.False);
            Assert.That(finalRow.TextSpan, Is.SameAs(final));
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
    public void ImageOutput_WhenProjected_UsesStronglyTypedRow()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        var span = new AssistantChatMessageImageSpan();
        assistant.AddSpan(span);
        using var context = Context(new UserChatMessage("Image", []), assistant);

        var row = context.Presentation.Rows.OfType<AssistantImageOutputPresentationRow>().Single();

        Assert.That(row.ImageSpan, Is.SameAs(span));
    }

    [AvaloniaTest]
    public async Task MarkdownSource_WhenFinalUpdateArrivesBeforeCompletion_DetachesAfterCompletion()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var span = new AssistantChatMessageTextSpan("Streaming");
        assistant.AddSpan(span);
        using var context = Context(new UserChatMessage("Render", []), assistant);
        var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var builder = span.ContentMarkdownBuilder;
        var snapshot = builder.CaptureSnapshot();
        row.CachedDocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(snapshot.Text), snapshot.Version);

        Assert.That(row.RenderingMarkdownBuilder, Is.SameAs(builder));

        span.FinishedAt = DateTimeOffset.UtcNow;
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.That(row.RenderingMarkdownBuilder, Is.Null);
    }

    [AvaloniaTest]
    public async Task MarkdownSource_WhenCompletionArrivesBeforeFinalUpdate_DetachesAfterUpdate()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var span = new AssistantChatMessageTextSpan("Streaming");
        assistant.AddSpan(span);
        using var context = Context(new UserChatMessage("Render", []), assistant);
        var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var builder = span.ContentMarkdownBuilder;
        span.FinishedAt = DateTimeOffset.UtcNow;
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.That(row.RenderingMarkdownBuilder, Is.SameAs(builder));

        var snapshot = builder.CaptureSnapshot();
        row.CachedDocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(snapshot.Text), snapshot.Version);

        Assert.That(row.RenderingMarkdownBuilder, Is.Null);
    }

    [AvaloniaTest]
    public async Task MarkdownRenderer_WhenProducerCommitsFinalUpdate_CachesUpdateAndDetachesBuilder()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var span = new AssistantChatMessageTextSpan("Streaming");
        assistant.AddSpan(span);
        using var context = Context(new UserChatMessage("Render", []), assistant);
        var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var updateCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantTextOutputPresentationRow.CachedDocumentUpdate))
                updateCommitted.TrySetResult();
        };

        var renderer = new MarkdownRenderer();
        using var documentBinding = renderer.Bind(
            MarkdownRenderer.DocumentUpdateProperty,
            new Binding(nameof(AssistantTextOutputPresentationRow.CachedDocumentUpdate))
            {
                Source = row,
                Mode = BindingMode.TwoWay,
            });
        using var builderBinding = renderer.Bind(
            MarkdownRenderer.MarkdownBuilderProperty,
            new Binding(nameof(AssistantTextOutputPresentationRow.RenderingMarkdownBuilder)) { Source = row });

        await updateCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(row.RenderingMarkdownBuilder, Is.SameAs(span.ContentMarkdownBuilder));

        span.FinishedAt = DateTimeOffset.UtcNow;
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Multiple(() =>
        {
            Assert.That(renderer.DocumentUpdate, Is.SameAs(row.CachedDocumentUpdate));
            Assert.That(renderer.MarkdownBuilder, Is.Null);
        });
    }

    [Test]
    public void InitialWindow_WhenHistoryExceedsBatchSize_MaterializesOnlyTailTurns()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize + 4);
        using (context)
        {
            var presentation = context.Presentation;
            var users = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(row => ((UserChatMessage)row.Node.Message).Content)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(users[0], Is.EqualTo("Question 4"));
                Assert.That(users[^1], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize + 3}"));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
            });
        }
    }

    [AvaloniaTest]
    public async Task LoadEarlier_WhenBatchIsPrepared_PrependsCompleteTurnsWithCachedMarkdown()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize + 4);
        using (context)
        {
            var presentation = context.Presentation;

            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            Assert.That(await presentation.LoadEarlierAsync(), Is.True);

            var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();
            var outputs = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize + 4));
                Assert.That(((UserChatMessage)users[0].Node.Message).Content, Is.EqualTo("Question 0"));
                Assert.That(presentation.HasEarlierTurns, Is.False);
                Assert.That(outputs, Has.All.Matches<AssistantTextOutputPresentationRow>(row =>
                    row.CachedDocumentUpdate?.Version == row.TextSpan.ContentMarkdownBuilder.Version));
                Assert.That(outputs, Has.All.Matches<AssistantTextOutputPresentationRow>(row => row.RenderingMarkdownBuilder is null));
            });
        }
    }

    [AvaloniaTest]
    public async Task Reveal_WhenTargetIsOutsideWindow_ReplacesWindowAroundLogicalTurn()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        using (context)
        {
            var presentation = context.Presentation;
            var target = targets[2];

            var row = await presentation.RevealAsync(target.Node, target.Span);

            var users = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(item => ((UserChatMessage)item.Node.Message).Content)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(row, Is.TypeOf<AssistantTextOutputPresentationRow>());
                Assert.That(((AssistantTextOutputPresentationRow)row!).TextSpan, Is.SameAs(target.Span));
                Assert.That(((AssistantTextOutputPresentationRow)row).CachedDocumentUpdate, Is.Not.Null);
                Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(users[0], Is.EqualTo("Question 0"));
                Assert.That(users[^1], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize - 1}"));
                Assert.That(presentation.HasEarlierTurns, Is.False);
                Assert.That(presentation.HasLaterTurns, Is.True);
            });
        }
    }

    [AvaloniaTest]
    public async Task LoadLater_AfterDistantReveal_AppendsNextCompleteBatch()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        using (context)
        {
            var presentation = context.Presentation;
            await presentation.RevealAsync(targets[2].Node, targets[2].Span);

            Assert.That(await presentation.LoadLaterAsync(), Is.True);

            var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize * 2));
                Assert.That(((UserChatMessage)users[^1].Node.Message).Content,
                    Is.EqualTo($"Question {ChatPresentation.TurnBatchSize * 2 - 1}"));
                Assert.That(presentation.HasLaterTurns, Is.True);
            });
        }
    }

    [AvaloniaTest]
    public async Task ShowLatest_AfterDistantReveal_ReplacesWindowWithBoundedTail()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        using (context)
        {
            var presentation = context.Presentation;
            var oldRow = await presentation.RevealAsync(targets[2].Node, targets[2].Span);

            Assert.That(await presentation.ShowLatestAsync(), Is.True);

            var users = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(row => ((UserChatMessage)row.Node.Message).Content)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(users[0], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize * 2}"));
                Assert.That(users[^1], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize * 3 - 1}"));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
                Assert.That(presentation.Rows.Any(row => ReferenceEquals(row, oldRow)), Is.False);
            });
        }
    }

    [AvaloniaTest]
    public async Task ShowLatest_WhenExpandedWindowContainsTail_ReplacesItWithExactTailWindow()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize + 4);
        using (context)
        {
            var presentation = context.Presentation;
            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            Assert.That(await presentation.LoadEarlierAsync(), Is.True);
            Assert.That(presentation.IsAtLatest, Is.True);

            Assert.That(await presentation.ShowLatestAsync(), Is.True);

            var users = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(row => ((UserChatMessage)row.Node.Message).Content)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(users[0], Is.EqualTo("Question 4"));
                Assert.That(users[^1], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize + 3}"));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
            });
        }
    }

    [AvaloniaTest]
    public async Task CompactAround_WhenMiddleTurnsAreVisible_KeepsBoundedWindowAroundThem()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        using (context)
        {
            var presentation = context.Presentation;
            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            Assert.That(await presentation.LoadEarlierAsync(), Is.True);
            Assert.That(await presentation.LoadEarlierAsync(), Is.True);
            var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();

            Assert.That(presentation.CompactAround(users[9], users[11]), Is.True);

            var retained = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(row => ((UserChatMessage)row.Node.Message).Content)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(retained, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(retained[0], Is.EqualTo("Question 6"));
                Assert.That(retained[^1], Is.EqualTo("Question 13"));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.True);
            });
        }
    }

    [AvaloniaTest]
    public async Task CompactAround_WhenViewportIntersectsTail_EndsAtLatestTurn()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        using (context)
        {
            var presentation = context.Presentation;
            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            Assert.That(await presentation.LoadEarlierAsync(), Is.True);
            Assert.That(await presentation.LoadEarlierAsync(), Is.True);
            var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();

            Assert.That(presentation.CompactAround(users[^4], users[^1]), Is.True);

            var retained = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(row => ((UserChatMessage)row.Node.Message).Content)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(retained, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(retained[0], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize * 2}"));
                Assert.That(retained[^1], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize * 3 - 1}"));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
            });
        }
    }

    [AvaloniaTest]
    public async Task CompactAround_WhenEarlierLoadIsInProgress_PreventsObsoleteExpansion()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 2);
        using (context)
        {
            var presentation = context.Presentation;
            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();

            var earlierLoad = presentation.LoadEarlierAsync();
            Assert.That(presentation.CompactAround(users[0], users[^1]), Is.False);
            Assert.That(await earlierLoad, Is.False);

            var retained = presentation.Rows
                .OfType<ChatMessagePresentationRow>()
                .Select(row => ((UserChatMessage)row.Node.Message).Content)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(retained, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(retained[0], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize}"));
                Assert.That(retained[^1], Is.EqualTo($"Question {ChatPresentation.TurnBatchSize * 2 - 1}"));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
            });
        }
    }

    [AvaloniaTest]
    public async Task BranchChange_WhenCompletedOutputBecomesVisible_PublishesCachedDocumentFirst()
    {
        var originalAssistant = new AssistantChatMessage
        {
            IsBusy = false,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        originalAssistant.AddSpan(FinishedText("Original"));
        using var context = Context(new UserChatMessage("Choose", []), originalAssistant);
        var originalNode = context.Items[^1];
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

        var alternateAssistant = new AssistantChatMessage
        {
            IsBusy = false,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        var alternateSpan = FinishedText("Alternate");
        alternateAssistant.AddSpan(alternateSpan);
        var alternatePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedWithoutCache = false;
        presentation.Rows.CollectionChanged += (_, _) =>
        {
            var row = presentation.Rows
                .OfType<AssistantTextOutputPresentationRow>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.TextSpan, alternateSpan));
            if (row is null) return;

            publishedWithoutCache |= row.CachedDocumentUpdate is null;
            alternatePublished.TrySetResult();
        };

        context.CreateBranchOn(originalNode, alternateAssistant);
        await alternatePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var alternateRow = presentation.Rows
            .OfType<AssistantTextOutputPresentationRow>()
            .Single(row => ReferenceEquals(row.TextSpan, alternateSpan));
        Assert.Multiple(() =>
        {
            Assert.That(publishedWithoutCache, Is.False);
            Assert.That(alternateRow.CachedDocumentUpdate, Is.Not.Null);
            Assert.That(alternateRow.RenderingMarkdownBuilder, Is.Null);
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
        presentation.Rows.CollectionChanged += (_, _) => collectionEvents++;

        function.Content = "A newer preview";

        Assert.Multiple(() =>
        {
            Assert.That(collectionEvents, Is.Zero);
            Assert.That(presentation.Rows, Has.Count.EqualTo(before.Length));
            Assert.That(presentation.Rows.Zip(before).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
            Assert.That(group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single().FunctionCall.Content,
                Is.EqualTo("A newer preview"));
        });
    }

    [AvaloniaTest]
    public async Task WorkerPropertyNotification_IsAppliedOnUiThread()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var function = FunctionMessage("Read", 1, true);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function));
        using var context = Context(new UserChatMessage("Worker refresh", []), assistant);
        var presentation = context.Presentation;
        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var item = group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();

        // FunctionCallChatMessage may be updated by the streaming worker. The projection must not
        // inspect or mutate its UI-owned flags on that worker; the dispatcher pass should refresh
        // the same row instance after the notification crosses the boundary.
        await Task.Run(() => function.Content = "worker preview");
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Multiple(() =>
        {
            Assert.That(item, Is.SameAs(group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single()));
            Assert.That(item.FunctionCall.Content, Is.EqualTo("worker preview"));
            Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>().Single(), Is.SameAs(group));
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
        presentation.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

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
        var visibleOutput = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        Assert.That(visibleOutput.TextSpan, Is.SameAs(final));

        presentation.Rows.OfType<ProcessSummaryPresentationRow>().Single().IsExpanded = true;
        var outputs = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(outputs, Has.Count.EqualTo(2));
            Assert.That(outputs[0].TextSpan, Is.SameAs(partial));
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
    public void InterruptedReasoning_LoadedFromHistory_IsNotRunning()
    {
        var assistant = new AssistantChatMessage { IsBusy = false };
        assistant.AddSpan(new AssistantChatMessageReasoningSpan("Interrupted reasoning"));
        using var context = Context(new UserChatMessage("Interrupted", []), assistant);
        var presentation = context.Presentation;

        var reasoning = presentation.Rows.OfType<ReasoningActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(reasoning.FinishedAt, Is.Null, "Presentation must not manufacture a persisted completion timestamp.");
            Assert.That(reasoning.IsRunning, Is.False, "A missing timestamp does not survive as live work after restart.");
            Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>(), Is.Empty);
            Assert.That(presentation.Rows.OfType<NoResponsePresentationRow>(), Has.Exactly(1).Items);
        });
    }

    [AvaloniaTest]
    public void UnfinishedReasoning_FollowsOwningAssistantRuntimeState()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        assistant.AddSpan(new AssistantChatMessageReasoningSpan("Active reasoning"));
        using var context = Context(new UserChatMessage("Active", []), assistant);
        var presentation = context.Presentation;

        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var reasoning = group.Items.OfType<ReasoningActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(reasoning.IsRunning, Is.True);
            Assert.That(group.IsRunning, Is.True);
        });

        assistant.IsBusy = false;

        Assert.Multiple(() =>
        {
            Assert.That(reasoning.IsRunning, Is.False);
            Assert.That(group.IsRunning, Is.False);
            Assert.That(reasoning.FinishedAt, Is.Null, "Presentation must not manufacture a persisted completion timestamp.");
        });
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
            Assert.That(item.FunctionCall.Calls.Length, Is.EqualTo(1));
            Assert.That(ChatActivityStatistics.Calculate([item]).SubagentCount, Is.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public async Task ContextOwnedBusyActivity_MorphsFromRunningGroupToDirectRow_WithoutMutatingSpans()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        using var context = Context(new UserChatMessage("Prepare tools", []), assistant);
        var presentation = context.Presentation;
        var busyActivity = await context.Presentation.SetBusyActivityAsync(
            LucideIconKind.Server,
            new DirectLocaleKey("Starting MCP"),
            removeAfterCompletion: false);

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

    [AvaloniaTest]
    public async Task TransientBusyActivity_WhenCompleted_IsRemovedFromPresentation()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        using var context = Context(new UserChatMessage("Prepare tools", []), assistant);
        var presentation = context.Presentation;
        var busyActivity = await context.Presentation.SetBusyActivityAsync(
            LucideIconKind.Hammer,
            new DirectLocaleKey("Generating tool call"),
            removeAfterCompletion: true);

        Assert.That(
            presentation.Rows
                .OfType<ActivityGroupPresentationRow>()
                .Single()
                .Items
                .OfType<BusyActivityItemPresentationRow>(),
            Has.Exactly(1).Items);

        busyActivity.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>(), Is.Empty);
            Assert.That(presentation.Rows.OfType<BusyActivityItemPresentationRow>(), Is.Empty);
            Assert.That(assistant.Spans, Is.Empty);
        });
    }

    [AvaloniaTest]
    public async Task ActivityTemplates_WhenSourcesChange_UpdateWithoutRowForwarding()
    {
        using var function = FunctionMessage("Read", 1, true);
        function.HeaderKey = null;
        var previewSlot = function.RegisterActivityPresentation("preview");
        var functionRow = new FunctionCallActivityItemPresentationRow(function, new DirectLocaleKey("Fallback"));
        var functionMarker = BuildActivityMarker(functionRow);
        var functionHeader = functionMarker.Header as TextBlock ?? throw new InvalidOperationException();
        var assistant = new AssistantChatMessage { IsBusy = true };
        var reasoning = new AssistantChatMessageReasoningSpan("Thinking");
        var reasoningRow = new ReasoningActivityItemPresentationRow(assistant, reasoning, new DirectLocaleKey("Reasoning"));
        var reasoningMarker = BuildActivityMarker(reasoningRow);
        using var context = Context(new UserChatMessage("Work", []), assistant);
        var activity = await context.Presentation.SetBusyActivityAsync(LucideIconKind.Server, new DirectLocaleKey("Starting"), false);
        var busyRow = context.Presentation.Rows.OfType<ActivityGroupPresentationRow>().Single()
            .Items.OfType<BusyActivityItemPresentationRow>().Single();
        Assert.That(activity, Is.SameAs(busyRow));
        var busyMarker = BuildActivityMarker(busyRow);
        var busyHeader = busyMarker.Header as TextBlock ?? throw new InvalidOperationException();
        var rowNotifications = 0;
        functionRow.PropertyChanged += (_, _) => rowNotifications++;
        reasoningRow.PropertyChanged += (_, _) => rowNotifications++;
        var window = new Window { Content = new StackPanel { Children = { functionMarker, reasoningMarker, busyMarker } } };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Multiple(() =>
            {
                Assert.That(functionHeader.Text, Is.EqualTo("Fallback"));
                Assert.That(functionMarker.IsSecondaryHeaderVisible, Is.False);
                Assert.That(reasoningMarker.IsRunning, Is.True);
                Assert.That(reasoningMarker.IsSecondaryHeaderVisible, Is.True);
                Assert.That(busyMarker.IsRunning, Is.True);
                Assert.That(busyMarker.IsSecondaryHeaderVisible, Is.False);
            });
            await Task.Run(() =>
            {
                function.HeaderKey = new DirectLocaleKey("Updated");
                previewSlot.Preview = new ChatPluginTextActivityPreview(new DirectLocaleKey("New preview"));
                function.DisplaySink.AppendText("Result");
            });
            activity.HeaderKey = new DirectLocaleKey("Connecting");
            activity.SecondaryHeaderKey = new DirectLocaleKey("Details");
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(functionHeader.Text, Is.EqualTo("Updated"));
                Assert.That(functionMarker.IsSecondaryHeaderVisible, Is.True);
                Assert.That(functionMarker.IsExpandable, Is.True);
                Assert.That(busyHeader.Text, Is.EqualTo("Connecting"));
                Assert.That(busyMarker.IsSecondaryHeaderVisible, Is.True);
                Assert.That(rowNotifications, Is.Zero);
            });
            function.IsBusy = false;
            assistant.IsBusy = false;
            activity.Dispose();
            Assert.Multiple(() =>
            {
                Assert.That(functionMarker.IsRunning, Is.False);
                Assert.That(functionMarker.IsSecondaryHeaderVisible, Is.False);
                Assert.That(reasoningMarker.IsRunning, Is.False);
                Assert.That(reasoningMarker.IsSecondaryHeaderVisible, Is.False);
                Assert.That(busyMarker.IsRunning, Is.False);
                Assert.That(reasoning.FinishedAt, Is.Null);
                Assert.That(rowNotifications, Is.Zero);
            });
        }
        finally
        {
            function.UnregisterActivityPresentation("preview", previewSlot);
            activity.Dispose();
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task BusyActivities_WhenCompletedOutsideWindow_RemoveTransientAndRetainCompletedRow()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        using (context)
        {
            var presentation = context.Presentation;
            var owner = (AssistantChatMessage)targets[^1].Node.Message;
            owner.IsBusy = true;
            var transient = await presentation.SetBusyActivityAsync(LucideIconKind.WifiSync, new DirectLocaleKey("Retry"), true, owner: owner);
            var retained = await presentation.SetBusyActivityAsync(LucideIconKind.Server, new DirectLocaleKey("Startup"), false, owner: owner);
            Assert.That(retained, Is.InstanceOf<BusyActivityItemPresentationRow>());
            var first = targets[0];
            Assert.That(await presentation.RevealAsync(first.Node, first.Span), Is.Not.Null);
            Assert.That(presentation.Rows.OfType<TurnFooterPresentationRow>().Any(row => ReferenceEquals(row.AssistantMessage, owner)), Is.False);

            await Task.Run(() =>
            {
                transient.Dispose();
                retained.Dispose();
                owner.IsBusy = false;
            });
            Dispatcher.UIThread.RunJobs();
            // Verify storage cleanup while its turn is absent, rather than merely hiding a stale
            // finished activity when the turn is later materialized.
            var storage = typeof(ChatPresentation).GetField("_busyActivitiesByNode", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(presentation) as Dictionary<ChatMessageNode, List<BusyActivityItemPresentationRow>>
                ?? throw new InvalidOperationException();
            Assert.That(storage[targets[^1].Node], Is.EqualTo(new[] { retained }));
            Assert.That(await presentation.ShowLatestAsync(), Is.True);
            var activities = presentation.Rows.OfType<ActivityGroupPresentationRow>().SelectMany(group => group.Items)
                .Concat(presentation.Rows.OfType<ActivityItemPresentationRow>()).OfType<BusyActivityItemPresentationRow>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(activities, Is.EqualTo(new[] { retained }));
                Assert.That(retained.IsRunning, Is.False);
                Assert.That(retained.FinishedAt, Is.Not.Null);
            });
        }
    }

    private static ActivityMarker BuildActivityMarker(ActivityItemPresentationRow row)
    {
        var resource = new ResourceInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Views/Chat/ChatPresentationRowPresenter.axaml")
        };
        var theme = resource.Loaded[typeof(ChatPresentationRowPresenter)] as ControlTheme ?? throw new InvalidOperationException();
        var templates = theme.Setters.OfType<Setter>().Single(setter => setter.Property == ShadUI.BindingAssist.DataTemplatesProperty)
            .Value as IEnumerable<IDataTemplate> ?? throw new InvalidOperationException();
        var marker = templates.Single(template => template.Match(row)).Build(row) as ActivityMarker ?? throw new InvalidOperationException();
        marker.DataContext = row;
        return marker;
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

    private static (ChatContext Context, List<(ChatMessageNode Node, AssistantChatMessageTextSpan Span)> Targets)
        CreateTurnHistory(int turnCount)
    {
        var context = new ChatContext();
        var targets = new List<(ChatMessageNode, AssistantChatMessageTextSpan)>(turnCount);
        for (var index = 0; index < turnCount; index++)
        {
            context.Add(new UserChatMessage($"Question {index}", []));
            var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
            var span = FinishedText($"Answer {index}");
            assistant.AddSpan(span);
            context.Add(assistant);
            targets.Add((context.Items[^1], span));
        }

        return (context, targets);
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
            message.AddCall(new FunctionCallContent(
                functionName: title,
                pluginName: null,
                id: i.ToString(),
                arguments: new KernelArguments()));
        }

        return message;
    }
}
