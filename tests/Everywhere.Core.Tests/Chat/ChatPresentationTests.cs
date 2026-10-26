using System.Collections.Specialized;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Core.Tests.Statistics;
using Everywhere.I18N;
using Everywhere.Storage;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public class ChatPresentationTests
{
    [AvaloniaTest]
    public async Task BranchSelector_WhenUnselectedSiblingIsAppended_RefreshesWithoutReplacingRow()
    {
        await using var context = new RootChatContext(new ChatData(), null);
        var first = context.Data.Add(new UserChatMessage("original", []));
        await context.Presentation.PrepareInitialWindowAsync();
        var row = context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Single();
        await context.DispatchAsync(() => context.Data.Append(context.Data.RootNode, new UserChatMessage("alternative", []), shouldSelectChild: false));
        Dispatcher.UIThread.RunJobs();

        Assert.That(context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Single(), Is.SameAs(row));
        Assert.That(row.Branch.ChoiceCount, Is.EqualTo(2));
        Assert.That(row.Branch.ChoiceIndex, Is.Zero);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Data.BranchChanged += (_, _) => changed.TrySetResult();
        row.Branch.ChoiceIndex = 1;
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.That(context.Data.SelectedTurns.Single().UserNode, Is.Not.SameAs(first));
    }

    [AvaloniaTest]
    public async Task CanceledOutcome_WhenLaterUserTurnExists_DoesNotOfferContinueOnEarlierTurn()
    {
        await using var context = new RootChatContext(new ChatData(), null);
        context.Data.Add(new UserChatMessage("first", []));
        context.Data.Add(new AssistantChatMessage { IsCanceled = true });
        await context.Presentation.PrepareInitialWindowAsync();
        var row = context.Presentation.Rows.OfType<AssistantCanceledPresentationRow>().Single();
        Assert.That(row.IsContinuationTarget, Is.True);
        context.Data.Add(new UserChatMessage("next", []));
        Dispatcher.UIThread.RunJobs();

        Assert.That(context.Presentation.Rows.OfType<AssistantCanceledPresentationRow>().Single(), Is.SameAs(row));
        Assert.That(row.IsContinuationTarget, Is.False);
    }

    [AvaloniaTest]
    public async Task RunningTurn_PreservesActivityAndOutputOrder()
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
        await using var context = Context(new UserChatMessage("Inspect this", []), action, assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
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

    [AvaloniaTest]
    public async Task CompletedTurn_PromotesOnlyTrailingFormalOutput()
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
        await using var context = Context(new UserChatMessage("Do work", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
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

    [AvaloniaTest]
    public async Task CompletedTurn_WithOneActivity_OmitsProcessSummary()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        assistant.AddSpan(FinishedReasoning("Think"));
        assistant.AddSpan(FinishedText("Final"));
        await using var context = Context(new UserChatMessage("One activity", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ProcessSummaryPresentationRow>(), Is.Empty);
            Assert.That(presentation.Rows.OfType<ReasoningActivityItemPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<AssistantOutputPresentationRow>(), Has.Exactly(1).Items);
            Assert.That(presentation.Rows.OfType<TurnFooterPresentationRow>(), Has.Exactly(1).Items);
        });
    }

    [AvaloniaTest]
    public async Task Markdown_WhenFinalParsePrecedesCompletion_KeepsDocumentAndStableRow()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var span = new AssistantChatMessageTextSpan("Streaming");
        assistant.AddSpan(span);
        await using var context = Context(new UserChatMessage("Render", []), assistant);
        await context.Presentation.PrepareInitialWindowAsync();
        var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        await WaitForMarkdownAsync(row);
        var document = row.Markdown.DocumentUpdate;

        span.FinishedAt = DateTimeOffset.UtcNow;
        assistant.IsBusy = false;
        Dispatcher.UIThread.RunJobs();

        Assert.That(context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single(), Is.SameAs(row));
        Assert.That(row.Markdown.DocumentUpdate, Is.SameAs(document));
    }

    [AvaloniaTest]
    public async Task Markdown_WhenCompletionPrecedesFinalParse_StillPublishesFinalText()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var span = new AssistantChatMessageTextSpan("Streaming");
        assistant.AddSpan(span);
        await using var context = Context(new UserChatMessage("Render", []), assistant);
        await context.Presentation.PrepareInitialWindowAsync();
        var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        await Task.Run(() =>
        {
            span.ContentMarkdownBuilder.Append(" final text");
            span.FinishedAt = DateTimeOffset.UtcNow;
            assistant.IsBusy = false;
        });
        await WaitForMarkdownAsync(row);

        Assert.That(row.Markdown.DocumentUpdate?.Version, Is.EqualTo(span.ContentMarkdownBuilder.Version));
        Assert.That(row.IsFinal, Is.True);
    }

    private static async Task WaitForMarkdownAsync(AssistantTextOutputPresentationRow row)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (row.Markdown.DocumentUpdate?.Version != row.TextSpan.ContentMarkdownBuilder.Version)
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    [AvaloniaTest]
    public async Task PrepareInitialWindow_WhenHistoryIsUnloaded_StartsOnlyOnRequestAndPublishesPreheatedRows()
    {
        using var database = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(database.Factory);
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(FinishedText("Saved **answer**"));
        await using var saved = Context(new UserChatMessage("Saved question", []), assistant);
        await storage.SaveDataAsync(saved.Data);
        using var loaded = await storage.GetChatContextAsync(saved.Data.Metadata);
        await using var runtime = new RootChatContext(loaded, storage);
        var node = loaded.SelectedTurns.Single().UserNode;
        var presentation = runtime.Presentation;
        var rows = presentation.Rows;

        Assert.Multiple(() =>
        {
            Assert.That(node.IsMessageLoaded, Is.False);
            Assert.That(rows, Is.Empty);
            Assert.That(presentation.IsWindowOperationActive, Is.False);
            Assert.That(presentation.HasMessages, Is.True);
            Assert.That(presentation.HasMultipleMessages, Is.True);
        });

        Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
        var output = rows.OfType<AssistantTextOutputPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows, Is.SameAs(rows));
            Assert.That(rows.OfType<ChatMessagePresentationRow>().Single().Node, Is.SameAs(node));
            Assert.That(node.IsMessageLoaded, Is.True);
            Assert.That(output.Markdown.DocumentUpdate?.Version, Is.EqualTo(output.TextSpan.ContentMarkdownBuilder.Version));
        });
    }

    [AvaloniaTest]
    public async Task ShowLatest_AfterLoadingEarlier_PreparesCompleteTurnsAndRestoresBoundedTail()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize + 4);
        await using (context)
        {
            var presentation = context.Presentation;
            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            var tailRows = presentation.Rows.ToArray();
            Assert.That(tailRows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
            Assert.That(presentation.HasEarlierTurns, Is.True);
            Assert.That(presentation.HasLaterTurns, Is.False);

            Assert.That(await presentation.LoadEarlierAsync(), Is.True);
            var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();
            var outputs = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(users, Has.Length.EqualTo(targets.Count));
                Assert.That(outputs.Select(row => row.TextSpan), Is.EqualTo(targets.Select(target => target.Span)));
                Assert.That(presentation.HasEarlierTurns, Is.False);
                Assert.That(presentation.IsAtLatest, Is.True);
                Assert.That(outputs, Has.All.Matches<AssistantTextOutputPresentationRow>(row =>
                    row.Markdown.DocumentUpdate?.Version == row.TextSpan.ContentMarkdownBuilder.Version));
            });

            Assert.That(await presentation.ShowLatestAsync(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Rows, Is.EqualTo(tailRows));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
            });
        }
    }

    [AvaloniaTest]
    public async Task LoadEarlier_WhenPreheatedTurnStreams_PublishesPreparedRows()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize + 4);
        await using (context)
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            var loading = presentation.LoadEarlierAsync();
            targets[0].Span.ContentMarkdownBuilder.Append("\nMore content while preparing.");

            Assert.That(await loading.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            var row = presentation.Rows.OfType<AssistantTextOutputPresentationRow>()
                .Single(candidate => ReferenceEquals(candidate.TextSpan, targets[0].Span));
            Assert.That(row.Markdown.DocumentUpdate, Is.Not.Null);
            await row.Markdown.PrepareAsync(CancellationToken.None);
            Assert.That(row.Markdown.DocumentUpdate?.Version, Is.EqualTo(targets[0].Span.ContentMarkdownBuilder.Version));
        }
    }

    [AvaloniaTest]
    public async Task PrepareWindow_WhenCanceled_KeepsVisibleRowsAndTheirSharedParsing()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize + 4);
        await using (context)
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            var rows = presentation.Rows.ToArray();
            var output = rows.OfType<AssistantTextOutputPresentationRow>().Last();
            output.TextSpan.ContentMarkdownBuilder.Append("\nStill streaming.");
            output.Markdown.Refresh();
            using var cancellation = new CancellationTokenSource();
            var preparing = presentation.PrepareInitialWindowAsync(cancellation.Token);
            cancellation.Cancel();

            Assert.That(await preparing.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
            await output.Markdown.PrepareAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Rows, Is.EqualTo(rows));
                Assert.That(presentation.IsAtLatest, Is.True);
                Assert.That(output.Markdown.DocumentUpdate?.Version, Is.EqualTo(output.TextSpan.ContentMarkdownBuilder.Version));
            });
        }
    }

    [AvaloniaTest]
    public async Task PrepareWindow_WhenProvisionalSpanIsRemoved_DoesNotPublishObsoleteOutput()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var original = new AssistantChatMessageTextSpan("Provisional");
        assistant.AddSpan(original);
        await using var context = Context(new UserChatMessage("Retry request", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        original.ContentMarkdownBuilder.Append(" more");
        var preparing = presentation.PrepareInitialWindowAsync();
        assistant.TruncateSpans(0);
        var replacement = new AssistantChatMessageTextSpan("Replacement");
        assistant.AddSpan(replacement);
        Dispatcher.UIThread.RunJobs();

        Assert.That(await preparing.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        var row = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        Assert.That(row.TextSpan, Is.SameAs(replacement));
    }

    [AvaloniaTest]
    public async Task ShowLatest_AfterRevealAndLoadingLater_ReturnsFromPreparedHistoryToBoundedTail()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        await using (context)
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            var target = targets[2];
            var revealed = await presentation.RevealAsync(target.Node, target.Span);
            var row = revealed as AssistantTextOutputPresentationRow
                ?? throw new AssertionException("Expected the revealed assistant output.");
            Assert.Multiple(() =>
            {
                Assert.That(row.TextSpan, Is.SameAs(target.Span));
                Assert.That(row.Markdown.DocumentUpdate?.Version, Is.EqualTo(target.Span.ContentMarkdownBuilder.Version));
                Assert.That(presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(presentation.HasEarlierTurns, Is.False);
                Assert.That(presentation.HasLaterTurns, Is.True);
            });

            Assert.That(await presentation.LoadLaterAsync(), Is.True);
            var outputs = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize * 2));
                Assert.That(outputs.Select(output => output.TextSpan),
                    Is.EqualTo(targets.Take(ChatPresentation.TurnBatchSize * 2).Select(item => item.Span)));
                Assert.That(outputs, Does.Contain(row));
                Assert.That(outputs, Has.All.Matches<AssistantTextOutputPresentationRow>(output =>
                    output.Markdown.DocumentUpdate?.Version == output.TextSpan.ContentMarkdownBuilder.Version));
                Assert.That(presentation.HasLaterTurns, Is.True);
            });

            Assert.That(await presentation.ShowLatestAsync(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
                Assert.That(presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Select(output => output.TextSpan),
                    Is.EqualTo(targets.TakeLast(ChatPresentation.TurnBatchSize).Select(item => item.Span)));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.False);
                Assert.That(presentation.Rows, Does.Not.Contain(row));
            });
        }
    }

    [AvaloniaTest]
    public async Task CompactAround_WhenEarlierLoadIsInProgress_PreventsObsoleteExpansion()
    {
        var (context, _) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 2);
        await using (context)
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
    public Task BranchChange_WhenShorterBranchIsPreheating_KeepsPublishedWindowAndRowOwners()
    {
        return AssertPublishedWindowDuringBranchPreparationAsync(0);
    }

    [AvaloniaTest]
    public Task BranchChange_WhenResponseIsPreheating_KeepsPublishedWindowAndRowOwners()
    {
        return AssertPublishedWindowDuringBranchPreparationAsync(ChatPresentation.TurnBatchSize * 3 - 1);
    }

    private static async Task AssertPublishedWindowDuringBranchPreparationAsync(int changedTurnIndex)
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        await using (context)
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            var rows = presentation.Rows.ToArray();
            var owners = rows.Select(presentation.GetUserTurnNode).ToArray();
            var alternate = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
            var alternateSpan = FinishedText("Alternate response");
            alternate.AddSpan(alternateSpan);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publishedWithoutCache = false;
            var publishedWithoutOwner = false;
            presentation.Rows.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is null) return;
                foreach (var row in e.NewItems.OfType<ChatPresentationRow>())
                {
                    publishedWithoutOwner |= presentation.GetUserTurnNode(row) is null;
                    if (row is AssistantTextOutputPresentationRow output && ReferenceEquals(output.TextSpan, alternateSpan))
                    {
                        publishedWithoutCache |= output.Markdown.DocumentUpdate is null;
                        published.TrySetResult();
                    }
                }
            };

            context.Data.CreateBranchOn(targets[changedTurnIndex].Node, alternate);
            // This callback is queued after repartition, but before its new parse can commit on
            // the UI thread. It exercises the same row lookup performed by a layout callback.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(presentation.IsWindowOperationActive, Is.True);
                    Assert.That(presentation.Rows, Is.EqualTo(rows));
                    Assert.That(rows.Select(presentation.GetUserTurnNode), Is.EqualTo(owners));
                    Assert.That(presentation.HasEarlierTurns, Is.True);
                    Assert.That(presentation.HasLaterTurns, Is.False);
                });
            });

            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var output = presentation.Rows.OfType<AssistantTextOutputPresentationRow>()
                .Single(row => ReferenceEquals(row.TextSpan, alternateSpan));
            Assert.Multiple(() =>
            {
                Assert.That(publishedWithoutCache, Is.False);
                Assert.That(publishedWithoutOwner, Is.False);
                Assert.That(presentation.GetUserTurnNode(output), Is.SameAs(targets[changedTurnIndex].Node.Turn?.UserNode));
                Assert.That(presentation.HasEarlierTurns, Is.EqualTo(changedTurnIndex > 0));
                Assert.That(presentation.IsAtLatest, Is.True);
            });

            foreach (var retained in rows.Where(presentation.Rows.Contains))
            {
                Assert.That(presentation.GetUserTurnNode(retained), Is.SameAs(owners[Array.IndexOf(rows, retained)]));
            }

            if (changedTurnIndex == targets.Count - 1)
            {
                var changedUser = targets[changedTurnIndex].Node.Turn?.UserNode;
                var unaffectedRows = rows.Where((_, index) => !ReferenceEquals(owners[index], changedUser));
                Assert.That(unaffectedRows.All(presentation.Rows.Contains), Is.True);
            }
        }
    }

    [AvaloniaTest]
    public async Task BranchChange_WhenPreparationIsCanceled_KeepsPublishedWindowAndCanRetry()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 3);
        await using (context)
        using (var cancellation = new CancellationTokenSource())
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            var rows = presentation.Rows.ToArray();
            var owners = rows.Select(presentation.GetUserTurnNode).ToArray();
            var alternate = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
            alternate.AddSpan(FinishedText("Short branch"));
            context.Data.CreateBranchOn(targets[0].Node, alternate);
            var isPrepared = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.That(presentation.IsWindowOperationActive, Is.True);
                var task = presentation.PrepareInitialWindowAsync(cancellation.Token);
                cancellation.Cancel();
                return task;
            });

            Assert.That(isPrepared, Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Rows, Is.EqualTo(rows));
                Assert.That(rows.Select(presentation.GetUserTurnNode), Is.EqualTo(owners));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.IsAtLatest, Is.True);
            });

            var visibleOutput = rows.OfType<AssistantTextOutputPresentationRow>().Last();
            visibleOutput.TextSpan.ContentMarkdownBuilder.Append("\nStill visible after cancellation.");
            await visibleOutput.Markdown.PrepareAsync(CancellationToken.None);
            Assert.That(visibleOutput.Markdown.DocumentUpdate?.Version, Is.EqualTo(visibleOutput.TextSpan.ContentMarkdownBuilder.Version));
            Assert.That(presentation.Rows, Does.Contain(visibleOutput));

            Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
            var user = presentation.Rows.OfType<ChatMessagePresentationRow>().Single();
            Assert.That(user.Node, Is.SameAs(targets[0].Node.Turn?.UserNode));
            Assert.That(presentation.HasEarlierTurns, Is.False);
            Assert.That(rows.Select(presentation.GetUserTurnNode), Has.All.Null);
        }
    }

    [AvaloniaTest]
    public async Task BranchChange_WhenPreparationFails_KeepsPublishedRowsAndCanRetry()
    {
        using var database = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(database.Factory);
        var (saved, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 2);
        await using var savedOwner = saved;
        await storage.SaveDataAsync(saved.Data);
        var data = await storage.GetChatContextAsync(saved.Data.Metadata);
        var loader = Substitute.For<IChatTurnLoader>();
        var failure = new IOException("Body loading failed");
        var shouldFail = false;
        loader.LoadMessagesAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(call =>
            shouldFail ? Task.FromException<IReadOnlyDictionary<Guid, ChatMessage>>(failure) :
                storage.LoadMessagesAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid?>(1), call.ArgAt<CancellationToken>(2)));
        await using var context = new RootChatContext(data, loader);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var rows = presentation.Rows.ToArray();
        var owners = rows.Select(presentation.GetUserTurnNode).ToArray();
        var earlier = data.FindNode(targets[0].Node.Id) ?? throw new AssertionException("Expected the cold branch point.");
        var alternate = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        var alternateSpan = FinishedText("Replacement response");
        alternate.AddSpan(alternateSpan);
        shouldFail = true;
        await context.DispatchAsync(() => data.CreateBranchOn(earlier, alternate));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        await Assert.ThatAsync(() => presentation.PrepareInitialWindowAsync(), Throws.Exception.SameAs(failure));
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows, Is.EqualTo(rows));
            Assert.That(rows.Select(presentation.GetUserTurnNode), Is.EqualTo(owners));
            Assert.That(presentation.HasEarlierTurns, Is.True);
            Assert.That(presentation.IsAtLatest, Is.True);
        });

        shouldFail = false;
        Assert.That(await presentation.PrepareInitialWindowAsync(), Is.True);
        var replacement = presentation.Rows.OfType<AssistantTextOutputPresentationRow>()
            .Single(row => ReferenceEquals(row.TextSpan, alternateSpan));
        Assert.That(replacement.Markdown.DocumentUpdate, Is.Not.Null);
        Assert.That(presentation.HasEarlierTurns, Is.False);
    }

    [AvaloniaTest]
    public async Task BranchChange_WhenPreparationIsSuperseded_PreservesPublishedWindowPositionAndIdentity()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 4);
        await using (context)
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            await presentation.RevealAsync(targets[12].Node, targets[12].Span);
            var rows = presentation.Rows.ToArray();
            var owners = rows.Select(presentation.GetUserTurnNode).ToArray();
            var original = targets[0].Node;
            var alternate = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
            alternate.AddSpan(FinishedText("Obsolete branch"));
            var collectionChanges = 0;
            presentation.Rows.CollectionChanged += (_, _) => collectionChanges++;

            context.Data.CreateBranchOn(original, alternate);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.That(presentation.IsWindowOperationActive, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.True);
                context.Data.SelectChild(original.Parent ?? throw new AssertionException("Expected a user parent."), original);
            });
            await Dispatcher.UIThread.InvokeAsync(() => Assert.That(presentation.Rows, Is.EqualTo(rows)));
            await presentation.PrepareInitialWindowAsync();

            Assert.Multiple(() =>
            {
                Assert.That(collectionChanges, Is.Zero);
                Assert.That(presentation.Rows, Is.EqualTo(rows));
                Assert.That(rows.Select(presentation.GetUserTurnNode), Is.EqualTo(owners));
                Assert.That(presentation.HasEarlierTurns, Is.True);
                Assert.That(presentation.HasLaterTurns, Is.True);
            });
        }
    }

    [AvaloniaTest]
    public async Task PrepareWindow_WhenSuperseded_CancelsBeforeReturningAndPublishesOnlyNewestTarget()
    {
        var (context, targets) = CreateTurnHistory(ChatPresentation.TurnBatchSize * 4);
        await using (context)
        {
            var presentation = context.Presentation;
            await presentation.PrepareInitialWindowAsync();
            var first = presentation.RevealAsync(targets[0].Node, targets[0].Span);
            var field = typeof(ChatPresentation).GetField("_windowOperationCancellation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var cancellation = field?.GetValue(presentation) as CancellationTokenSource
                ?? throw new AssertionException("Expected pending preparation.");
            var wasCanceledOnUiThread = false;
            using var registration = cancellation.Token.Register(() => wasCanceledOnUiThread = Dispatcher.UIThread.CheckAccess());

            var second = presentation.RevealAsync(targets[12].Node, targets[12].Span);
            Assert.That(wasCanceledOnUiThread, Is.True);
            var newest = presentation.RevealAsync(targets[20].Node, targets[20].Span);
            await Task.WhenAll(first, second, newest);

            Assert.That(await first, Is.Null);
            Assert.That(await second, Is.Null);
            Assert.That(await newest, Is.Not.Null);
            Assert.That(presentation.Rows.OfType<AssistantTextOutputPresentationRow>()
                .Any(row => ReferenceEquals(row.TextSpan, targets[20].Span)), Is.True);
            Assert.That(presentation.IsWindowOperationActive, Is.False);
        }
    }

    [AvaloniaTest]
    public async Task ExpandingGroupAndItem_OnlyChangesGroupVisualState()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var firstFunction = FunctionMessage("Read", 1, true);
        firstFunction.DisplaySink.AppendText("preview");
        var secondFunction = FunctionMessage("Read more", 1, true);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan([firstFunction, secondFunction]));
        await using var context = Context(new UserChatMessage("Expand", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
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

    [AvaloniaTest]
    public async Task RunningSingleActivity_KeepsSameExpandedGroup_WhenSiblingArrives()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var firstFunction = FunctionMessage("Read", 1, true);
        var functionSpan = new AssistantChatMessageFunctionCallSpan(firstFunction);
        assistant.AddSpan(functionSpan);
        await using var context = Context(new UserChatMessage("Promote", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var originalGroup = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var firstItem = originalGroup.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();
        var rowsBefore = presentation.Rows.ToArray();

        functionSpan.Add(FunctionMessage("Read more", 1, true));

        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        Dispatcher.UIThread.RunJobs();
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
        Dispatcher.UIThread.RunJobs();
        Assert.That(group.Items, Is.SameAs(items));
        Assert.That(group.Items, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task CompletedTool_KeepsOnlyTrailingGroupRunning_WhileAssistantAwaitsContinuation()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var function = FunctionMessage("Read", 1, false);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function)
        {
            FinishedAt = function.FinishedAt,
        });
        await using var context = Context(new UserChatMessage("Continue after tool", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

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
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(group.IsRunning, Is.False);
            Assert.That(group.IsExpanded, Is.True, "Completion must preserve the user's expansion choice.");
            Assert.That(group.FinishedAt, Is.Not.Null);
        });
    }

    [AvaloniaTest]
    public async Task RunningGroup_WhenUpdatedAndCompleted_PreservesUserExpansionAndSummaryPlacement()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        var first = FunctionMessage("Read", 1, true);
        var second = FunctionMessage("Read more", 1, true);
        var span = new AssistantChatMessageFunctionCallSpan(first);
        assistant.AddSpan(span);
        await using var context = Context(new UserChatMessage("Keep my choice", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var item = group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();
        Assert.That(group.IsExpanded, Is.True);

        group.IsExpanded = false;
        first.Content = "Updated preview";
        span.Add(second);
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>().Single(), Is.SameAs(group));
            Assert.That(group.IsExpanded, Is.False);
            Assert.That(group.Items[0], Is.SameAs(item));
            Assert.That(group.Items, Has.Count.EqualTo(2));
            Assert.That(item.State.Content, Is.EqualTo("Updated preview"));
        });

        group.IsExpanded = true;
        first.IsBusy = false;
        second.IsBusy = false;
        assistant.AddSpan(FinishedText("Done"));
        assistant.IsBusy = false;
        Dispatcher.UIThread.RunJobs();
        Assert.That(presentation.Rows, Does.Contain(group), "Keep the group in place during its completion animation.");

        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();
        var summary = presentation.Rows.OfType<ProcessSummaryPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(summary.IsExpanded, Is.False);
            Assert.That(presentation.Rows, Does.Not.Contain(group));
            Assert.That(group.IsExpanded, Is.True, "Final placement must not force the inner group closed.");
        });

        first.Content = "Final preview";
        summary.IsExpanded = true;
        Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>().Single(), Is.SameAs(group));
        Assert.That(group.IsExpanded, Is.True);
        Assert.That(item.State.Content, Is.EqualTo("Updated preview"), "Expansion only changes visibility of the captured rows.");
        Dispatcher.UIThread.RunJobs();
        Assert.That(item.State.Content, Is.EqualTo("Final preview"));
    }

    [AvaloniaTest]
    public async Task CompletedGroup_LoadedFromHistory_StartsCollapsed()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan([
            FunctionMessage("Read", 1, false),
            FunctionMessage("Read more", 1, false),
            FunctionMessage("Read final", 1, false),
        ]));
        assistant.AddSpan(FinishedText("Done"));
        await using var context = Context(new UserChatMessage("History", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

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

    [AvaloniaTest]
    public async Task UpdatingLaterTurn_DoesNotTouchEarlierTurnRows()
    {
        var firstAssistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        firstAssistant.AddSpan(FinishedText("First answer"));
        var secondAssistant = new AssistantChatMessage { IsBusy = true };
        await using var context = Context(
            new UserChatMessage("First", []),
            firstAssistant,
            new UserChatMessage("Second", []),
            secondAssistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var firstTurnRows = presentation.Rows.Take(3).ToArray();

        secondAssistant.AddSpan(FinishedReasoning("Working"));
        Dispatcher.UIThread.RunJobs();

        Assert.That(presentation.Rows.Take(3).Zip(firstTurnRows)
            .All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
    }

    [AvaloniaTest]
    public async Task Continue_DoesNotMergeFailedPartialOutputIntoFinalOutput()
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
        await using var context = Context(new UserChatMessage("Continue case", []), failed, continued);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
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

    [AvaloniaTest]
    public async Task Reveal_WhenCompressionSeparatesSummaries_ExpandsOnlyTargetSummary()
    {
        await using var context = new RootChatContext(new ChatData(), null);
        context.Data.Add(new UserChatMessage("Search partial output", []));
        var first = new AssistantChatMessage { IsCanceled = true };
        var firstText = FinishedText("Before compression");
        first.AddSpan(firstText);
        var firstNode = context.Data.Add(first);
        var compression = new ContextCompressionChatMessage(firstNode.Id, "model", ContextCompressionTrigger.Manual, null, null);
        compression.Complete("Saved summary", false, DateTimeOffset.UtcNow);
        context.Data.Add(compression);
        var second = new AssistantChatMessage { ErrorMessageKey = new DirectLocaleKey("failed") };
        var secondText = FinishedText("After compression");
        second.AddSpan(secondText);
        var secondNode = context.Data.Add(second);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var summaries = presentation.Rows.OfType<ProcessSummaryPresentationRow>().ToArray();
        Assert.That(summaries, Has.Length.EqualTo(2));

        var secondRow = await presentation.RevealAsync(secondNode, secondText);
        Assert.Multiple(() =>
        {
            Assert.That(secondRow, Is.TypeOf<AssistantTextOutputPresentationRow>());
            Assert.That(presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single().TextSpan, Is.SameAs(secondText));
            Assert.That(summaries[0].IsExpanded, Is.False);
            Assert.That(summaries[1].IsExpanded, Is.True);
        });
        Assert.That(await presentation.RevealAsync(secondNode, secondText), Is.SameAs(secondRow));

        summaries[1].IsExpanded = false;
        await presentation.RevealAsync(firstNode, firstText);
        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single().TextSpan, Is.SameAs(firstText));
            Assert.That(summaries[0].IsExpanded, Is.True);
            Assert.That(summaries[1].IsExpanded, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task SuccessfulEmptyTurn_DisplaysNoResponseRow()
    {
        await using var context = Context(
            new UserChatMessage("Empty", []),
            new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow });
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

        Assert.That(presentation.Rows.OfType<NoResponsePresentationRow>(), Has.Exactly(1).Items);
    }

    [AvaloniaTest]
    public async Task InterruptedReasoning_LoadedFromHistory_IsNotRunning()
    {
        var assistant = new AssistantChatMessage { IsBusy = false };
        assistant.AddSpan(new AssistantChatMessageReasoningSpan("Interrupted reasoning"));
        await using var context = Context(new UserChatMessage("Interrupted", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

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
    public async Task UnfinishedReasoning_FollowsOwningAssistantRuntimeState()
    {
        var assistant = new AssistantChatMessage { IsBusy = true };
        assistant.AddSpan(new AssistantChatMessageReasoningSpan("Active reasoning"));
        await using var context = Context(new UserChatMessage("Active", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();

        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        var reasoning = group.Items.OfType<ReasoningActivityItemPresentationRow>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(reasoning.IsRunning, Is.True);
            Assert.That(group.IsRunning, Is.True);
        });

        assistant.IsBusy = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(reasoning.IsRunning, Is.False);
            Assert.That(group.IsRunning, Is.False);
            Assert.That(reasoning.FinishedAt, Is.Null, "Presentation must not manufacture a persisted completion timestamp.");
        });
    }

    [AvaloniaTest]
    public async Task StructuredSubagentBlocks_DriveSubagentCount()
    {
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        var function = FunctionMessage("renamed_tool", 1, false);
        function.DisplaySink.AppendBlock(new ChatPluginSubagentDisplayBlock(new ChatData()));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function) { FinishedAt = DateTimeOffset.UtcNow });
        assistant.AddSpan(FinishedText("Done"));
        await using var context = Context(new UserChatMessage("Delegate", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
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
        await using var context = Context(new UserChatMessage("Prepare tools", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var busyActivity = context.Data.StartActivity(
            LucideIconKind.Server,
            new DirectLocaleKey("Starting MCP"),
            removeAfterCompletion: false);
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

        // Completing the item does not complete the trailing Group while the assistant invocation
        // is still waiting for continuation. The item remains truthful and only the Group carries
        // the turn-local continuation state.
        var waitingGroup = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        Assert.That(item.IsRunning, Is.False);
        Assert.That(waitingGroup.IsRunning, Is.True);
        Assert.That(waitingGroup.IsExpanded, Is.True);

        assistant.IsBusy = false;
        Dispatcher.UIThread.RunJobs();

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
        await using var context = Context(new UserChatMessage("Prepare tools", []), assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var busyActivity = context.Data.StartActivity(
            LucideIconKind.Hammer,
            new DirectLocaleKey("Generating tool call"),
            removeAfterCompletion: true);
        Dispatcher.UIThread.RunJobs();

        Assert.That(
            presentation.Rows
                .OfType<ActivityGroupPresentationRow>()
                .Single()
                .Items
                .OfType<BusyActivityItemPresentationRow>(),
            Has.Exactly(1).Items);

        busyActivity.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>(), Is.Empty);
            Assert.That(presentation.Rows.OfType<BusyActivityItemPresentationRow>(), Is.Empty);
            Assert.That(assistant.Spans, Is.Empty);
        });
    }

    [AvaloniaTest]
    public async Task ActivityTemplates_WhenSnapshotsRefresh_UpdateWithoutModelBindings()
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
        await using var context = Context(new UserChatMessage("Work", []), assistant);
        var activity = context.Data.StartActivity(LucideIconKind.Server, new DirectLocaleKey("Starting"), false);
        Dispatcher.UIThread.RunJobs();
        await context.Presentation.PrepareInitialWindowAsync();
        var busyRow = context.Presentation.Rows.OfType<ActivityGroupPresentationRow>().Single()
            .Items.OfType<BusyActivityItemPresentationRow>().Single();
        Assert.That(busyRow.Activity, Is.SameAs(activity));
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
            functionRow.Refresh();
            reasoningRow.Refresh();
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
                Assert.That(rowNotifications, Is.GreaterThan(0));
            });
            function.IsBusy = false;
            assistant.IsBusy = false;
            Dispatcher.UIThread.RunJobs();
            activity.Dispose();
            Dispatcher.UIThread.RunJobs();
            functionRow.Refresh();
            reasoningRow.Refresh();
            Assert.Multiple(() =>
            {
                Assert.That(functionMarker.IsRunning, Is.False);
                Assert.That(functionMarker.IsSecondaryHeaderVisible, Is.False);
                Assert.That(reasoningMarker.IsRunning, Is.False);
                Assert.That(reasoningMarker.IsSecondaryHeaderVisible, Is.False);
                Assert.That(busyMarker.IsRunning, Is.False);
                Assert.That(reasoning.FinishedAt, Is.Null);
                Assert.That(rowNotifications, Is.GreaterThan(0));
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
        await using (context)
        {
            var presentation = context.Presentation;
            var owner = (AssistantChatMessage)targets[^1].Node.Message;
            owner.IsBusy = true;
            var transient = context.Data.StartActivity(LucideIconKind.WifiSync, new DirectLocaleKey("Retry"), true, owner: owner);
            var retained = context.Data.StartActivity(LucideIconKind.Server, new DirectLocaleKey("Startup"), false, owner: owner);
            Assert.That(retained, Is.InstanceOf<ChatBusyActivity>());
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
            Assert.That(targets[^1].Node.Turn?.Activities, Is.EqualTo(new[] { retained }));
            Assert.That(await presentation.ShowLatestAsync(), Is.True);
            var activities = presentation.Rows.OfType<ActivityGroupPresentationRow>().SelectMany(group => group.Items)
                .Concat(presentation.Rows.OfType<ActivityItemPresentationRow>()).OfType<BusyActivityItemPresentationRow>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(activities.Select(row => row.Activity), Is.EqualTo(new[] { retained }));
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

    private static RootChatContext Context(params ChatMessage[] messages)
    {
        var context = new RootChatContext(new ChatData(), null);
        foreach (var message in messages) context.Data.Add(message);
        return context;
    }

    private static (RootChatContext Context, List<(ChatMessageNode Node, AssistantChatMessageTextSpan Span)> Targets)
        CreateTurnHistory(int turnCount)
    {
        var context = new RootChatContext(new ChatData(), null);
        var targets = new List<(ChatMessageNode, AssistantChatMessageTextSpan)>(turnCount);
        for (var index = 0; index < turnCount; index++)
        {
            context.Data.Add(new UserChatMessage($"Question {index}", []));
            var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
            var span = FinishedText($"Answer {index}");
            assistant.AddSpan(span);
            context.Data.Add(assistant);
            targets.Add((context.Data.Items[^1], span));
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