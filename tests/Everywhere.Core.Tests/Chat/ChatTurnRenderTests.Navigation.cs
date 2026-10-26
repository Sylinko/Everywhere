using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.ViewModels;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using NSubstitute;
using Pointer = Avalonia.Input.Pointer;

namespace Everywhere.Core.Tests.Chat;

public sealed partial class ChatTurnRenderTests
{
    private static async Task Navigation_WhenTurnJumpSupersedesLatest_KeepsNewestTargetAndStableRows()
    {
        await using var context = CreateHistory(24);
        // Directly attached conversations (including subagent dialogs) use viewport navigation
        // for their first preparation instead of relying on construction or the manager.
        Assert.That(context.Presentation.Rows, Is.Empty);
        var items = new ChatMessageItemsControl { ChatContext = context, IsReadonly = true };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            var target = context.Data.SelectedTurns[3].UserNode;
            var latest = items.ShowLatestCommand.ExecuteAsync(null);
            var turn = items.RevealTurnAsync(target);
            await Task.WhenAll(latest, turn);
            await WaitForNavigationAsync(window, items);
            AssertTurnNearTop(items, scroll, target);
            Assert.That(context.Presentation.HasLaterTurns, Is.True);
            var retained = context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Single(row => row.Node == target);

            await context.Presentation.LoadLaterAsync();
            await items.RevealTurnAsync(target);
            await WaitForNavigationAsync(window, items);
            Assert.That(context.Presentation.Rows, Does.Contain(retained));
            Assert.That(context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
            AssertTurnNearTop(items, scroll, target);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task Navigation_WhenEdgeLoadIsSuperseded_DoesNotRestoreOldDirection()
    {
        await using var context = CreateHistory(32);
        await context.Presentation.PrepareInitialWindowAsync();
        var items = new ChatMessageItemsControl { ChatContext = context };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            scroll.Offset = default;
            SettleLayout(window);
            window.MouseWheel(new Point(200, 250), new Vector(0, 1));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await items.RevealTurnAsync(context.Data.SelectedTurns[12].UserNode);
            await WaitForNavigationAsync(window, items);
            var rows = context.Presentation.Rows.ToArray();
            var offset = scroll.Offset;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            SettleLayout(window);
            Assert.That(context.Presentation.Rows, Is.EqualTo(rows));
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset.Y).Within(0.5));
            Assert.That(rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task SearchNavigation_WhenSurfaceUpdatesAfterUserScroll_DoesNotRecenter()
    {
        await using var context = CreateHistory(24);
        await context.Presentation.PrepareInitialWindowAsync();
        var manager = Substitute.For<IChatContextManager>();
        manager.Current.Returns(context);
        using var search = new ChatTextSearchViewModel(manager) { Query = "Saved answer 12" };
        var items = new ChatMessageItemsControl { ChatContext = context, TextSearch = search };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            search.OpenSearchCommand.Execute(null);
            await WaitForViewportAsync(window, () => !search.IsBusy && search.HasMatches);
            await WaitForNavigationAsync(window, items);
            var match = search.GetCurrentMatch() ?? throw new AssertionException("Expected a search match.");
            var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single(candidate => candidate.TextSpan == match.Span);
            Assert.That(items.TextSearchSurfaceRegistry.TryGet(row, out var surface), Is.True);
            Assert.That(surface, Is.Not.Null);
            Assert.That(surface.TryGetMatchCenter(match.LocalIndex, scroll, out var center), Is.True);
            Assert.That(center.Y, Is.EqualTo(scroll.Viewport.Height / 2).Within(2));

            window.MouseWheel(new Point(250, 300), new Vector(0, -1));
            SettleLayout(window);
            var offset = scroll.Offset;
            items.TextSearchSurfaceRegistry.NotifyChanged(row);
            SettleLayout(window);
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset.Y).Within(0.5));

            var source = row.TextSpan.ContentMarkdownBuilder;
            var projection = new MarkdownTextProjector().Project(
                new ObservableStringBuilderSnapshot("prefix Saved answer 12", source.Version));
            search.AcceptRenderedProjection(row, source, projection);
            await WaitForViewportAsync(window, () => !search.IsBusy);
            Assert.That(search.GetCurrentMatch(), Is.Not.EqualTo(match));
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset.Y).Within(0.5));

            await items.ShowLatestCommand.ExecuteAsync(null);
            await WaitForNavigationAsync(window, items);
            items.TextSearchSurfaceRegistry.NotifyChanged(row);
            SettleLayout(window);
            AssertAtBottom(scroll);
            AssertReadingTurn(items);
            Assert.That(context.Presentation.IsAtLatest, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task SearchNavigation_WhenResultsArriveAfterNewerIntent_DoesNotTakeBackViewport()
    {
        await using var context = CreateHistory(24);
        await context.Presentation.PrepareInitialWindowAsync();
        var manager = Substitute.For<IChatContextManager>();
        manager.Current.Returns(context);
        using var search = new ChatTextSearchViewModel(manager) { Query = "Saved answer 2" };
        var items = new ChatMessageItemsControl { ChatContext = context, TextSearch = search };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            search.OpenSearchCommand.Execute(null);
            Assert.That(search.IsBusy, Is.True);
            await items.ShowLatestCommand.ExecuteAsync(null);
            await WaitForViewportAsync(window, () => !search.IsBusy);
            await WaitForNavigationAsync(window, items);
            AssertAtBottom(scroll);

            search.Query = "Saved answer 3";
            Assert.That(search.IsBusy, Is.True);
            window.MouseWheel(new Point(250, 300), new Vector(0, 1));
            SettleLayout(window);
            var offset = scroll.Offset;
            await WaitForViewportAsync(window, () => !search.IsBusy);
            await WaitForNavigationAsync(window, items);
            Assert.That(scroll.Offset.Y, Is.EqualTo(offset.Y).Within(0.5));
            Assert.That(context.Presentation.IsAtLatest, Is.True);

            // A newer query can still navigate, including across suspension while matching.
            search.Query = "Saved answer 4";
            window.Hide();
            await WaitForViewportAsync(window, () => !search.IsBusy);
            items.CompactCurrentViewport();
            window.Show();
            await WaitForNavigationAsync(window, items);
            var match = search.GetCurrentMatch() ?? throw new AssertionException("Expected the resumed search match.");
            var row = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single(candidate => candidate.TextSpan == match.Span);
            Assert.That(items.TextSearchSurfaceRegistry.TryGet(row, out var surface), Is.True);
            Assert.That(surface, Is.Not.Null);
            Assert.That(surface.TryGetMatchCenter(match.LocalIndex, scroll, out var center), Is.True);
            Assert.That(center.Y, Is.EqualTo(scroll.Viewport.Height / 2).Within(2));
        }
        finally
        {
            window.Close();
        }
    }

    private static Task HiddenWindow_WhenCompacted_RestoresVisibleReadingAnchor()
    {
        return VerifySuspendedReadingAsync(false);
    }

    private static Task MinimizedWindow_WhenCompacted_RestoresVisibleReadingAnchor()
    {
        return VerifySuspendedReadingAsync(true);
    }

    private static async Task VerifySuspendedReadingAsync(bool shouldMinimize)
    {
        await using var context = CreateHistory(32);
        await context.Presentation.PrepareInitialWindowAsync();
        var items = new ChatMessageItemsControl { ChatContext = context };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            await items.RevealTurnAsync(context.Data.SelectedTurns[12].UserNode);
            await WaitForNavigationAsync(window, items);
            await context.Presentation.LoadEarlierAsync();
            await context.Presentation.LoadLaterAsync();
            SettleLayout(window);
            var panel = items.ItemsPanelRoot as VariableHeightVirtualizingStackPanel ?? throw new AssertionException("Expected the virtualizing panel.");
            Assert.That(panel.TryGetViewportSnapshot(out var before), Is.True);
            var row = context.Presentation.Rows[before.AnchorIndex];
            var readingTurn = items.ReadingTurnNode;
            Assert.That(context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.GreaterThan(ChatPresentation.TurnBatchSize));

            if (shouldMinimize)
            {
                window.WindowState = WindowState.Minimized;
            }
            else
            {
                window.Hide();
            }
            items.CompactCurrentViewport();
            Assert.That(context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
            Assert.That(context.Presentation.Rows, Does.Contain(row));
            window.Width = 660;
            if (shouldMinimize)
            {
                window.WindowState = WindowState.Normal;
            }
            else
            {
                window.Show();
            }
            await WaitForNavigationAsync(window, items);
            Assert.That(panel.TryGetViewportSnapshot(out var after), Is.True);
            Assert.That(context.Presentation.Rows[after.AnchorIndex], Is.SameAs(row));
            Assert.That(after.OffsetWithinAnchor, Is.EqualTo(before.OffsetWithinAnchor).Within(2));
            Assert.That(items.ReadingTurnNode, Is.SameAs(readingTurn));
            SaveRender(window, "chatturn-restored-reading.png");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task HiddenWindow_WhenNavigationIsPending_ResumesTargetInsteadOfOldAnchor()
    {
        await using var context = CreateHistory(24);
        await context.Presentation.PrepareInitialWindowAsync();
        var items = new ChatMessageItemsControl { ChatContext = context };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            var target = context.Data.SelectedTurns[2].UserNode;
            var navigating = items.RevealTurnAsync(target);
            window.Hide();
            await navigating;
            items.CompactCurrentViewport();
            window.Show();
            await WaitForNavigationAsync(window, items);
            AssertTurnNearTop(items, scroll, target);
            Assert.That(context.Presentation.HasLaterTurns, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task HiddenWindow_WhenReadingBranchIsRemoved_FallsBackToLatestAndKeepsNavigationUsable()
    {
        await using var context = CreateHistory(24);
        await context.Presentation.PrepareInitialWindowAsync();
        var items = new ChatMessageItemsControl { ChatContext = context };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            await items.RevealTurnAsync(context.Data.SelectedTurns[12].UserNode);
            await WaitForNavigationAsync(window, items);
            window.Hide();
            context.Data.CreateBranchOn(context.Data.SelectedTurns[0].SelectedNodes[1], new AssistantChatMessage());
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            items.CompactCurrentViewport();
            window.Show();
            await WaitForNavigationAsync(window, items);
            Assert.That(context.Presentation.IsAtLatest, Is.True);
            Assert.That(context.Presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(1));
            await items.RevealTurnAsync(context.Data.SelectedTurns[0].UserNode);
            await WaitForNavigationAsync(window, items);
            Assert.That(items.ReadingTurnNode, Is.SameAs(context.Data.SelectedTurns[0].UserNode));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task Navigation_WhenUserScrollsDuringPreparation_CancelsTheJump()
    {
        await using var context = CreateHistory(24);
        await context.Presentation.PrepareInitialWindowAsync();
        var items = new ChatMessageItemsControl { ChatContext = context };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            var rows = context.Presentation.Rows.ToArray();
            var navigating = items.RevealTurnAsync(context.Data.SelectedTurns[2].UserNode);
            Assert.That(context.Presentation.IsWindowOperationActive, Is.True);
            // Headless MouseWheel drains dispatcher jobs before input, which can finish preparation first.
            using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            items.RaiseEvent(new PointerWheelEventArgs(
                items, pointer, window, new Point(250, 300), 0, new PointerPointProperties(), KeyModifiers.None, new Vector(0, 1)));
            await navigating;
            await WaitForNavigationAsync(window, items);
            Assert.That(context.Presentation.Rows, Is.EqualTo(rows));
            Assert.That(scroll.Offset.Y, Is.LessThan(scroll.ScrollBarMaximum.Y));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task HiddenWindow_WhenTailStreamsOrContextChanges_ResumesOnlyTheCurrentTail()
    {
        await using var context = CreateHistory(16);
        await using var replacement = CreateHistory(24);
        await context.Presentation.PrepareInitialWindowAsync();
        await replacement.Presentation.PrepareInitialWindowAsync();
        var items = new ChatMessageItemsControl { ChatContext = context };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateNavigationWindow(items, scroll);
        try
        {
            window.Show();
            await WaitForNavigationAsync(window, items);
            var output = context.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Last();
            window.Hide();
            output.TextSpan.ContentMarkdownBuilder.Append("\n\nMore output while the window is hidden.");
            await output.Markdown.PrepareAsync(CancellationToken.None);
            items.CompactCurrentViewport();
            window.Show();
            await WaitForNavigationAsync(window, items);
            AssertAtBottom(scroll);
            Assert.That(context.Presentation.Rows, Does.Contain(output));
            Assert.That(output.Markdown.DocumentUpdate?.Version, Is.EqualTo(output.TextSpan.ContentMarkdownBuilder.Version));

            var oldNavigation = items.RevealTurnAsync(context.Data.SelectedTurns[0].UserNode);
            window.Hide();
            items.ChatContext = replacement;
            await oldNavigation;
            items.CompactCurrentViewport();
            window.Show();
            await WaitForNavigationAsync(window, items);
            AssertAtBottom(scroll);
            Assert.That(items.ItemsView.Cast<ChatPresentationRow>(), Is.EqualTo(replacement.Presentation.Rows));
            Assert.That(replacement.Presentation.IsAtLatest, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateNavigationWindow(ChatMessageItemsControl items, ScrollViewer scroll)
    {
        var window = CreateWindow(scroll, 720, 620);
        items.SetViewportActive(false);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            {
                items.SetViewportActive(window.IsVisible && window.WindowState != WindowState.Minimized);
            }
        };
        return window;
    }

    private static async Task WaitForNavigationAsync(Window window, ChatMessageItemsControl items)
    {
        var target = typeof(ChatMessageItemsControl).GetField("_navigationTarget", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertionException("Expected the current navigation target.");
        await WaitForViewportAsync(window, () => target.GetValue(items) is null && items.ChatContext?.Presentation.IsWindowOperationActive != true);
    }

    private static async Task WaitForViewportAsync(Window window, Func<bool> isReady)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(10);
            SettleLayout(window);
            if (isReady())
            {
                return;
            }
        }
        Assert.Fail("The viewport did not settle.");
    }

    private static void AssertTurnNearTop(ChatMessageItemsControl items, ScrollViewer scroll, ChatMessageNode node)
    {
        var row = items.ChatContext?.Presentation.Rows.OfType<ChatMessagePresentationRow>().Single(candidate => candidate.Node == node)
            ?? throw new AssertionException("Expected the target row.");
        var container = items.ContainerFromIndex(items.ItemsView.IndexOf(row)) ?? throw new AssertionException("Expected a realized target.");
        Assert.That(container.TranslatePoint(default, scroll)?.Y, Is.EqualTo(12).Within(2));
    }

    private static void AssertReadingTurn(ChatMessageItemsControl items)
    {
        var panel = items.ItemsPanelRoot as VariableHeightVirtualizingStackPanel ?? throw new AssertionException("Expected the virtualizing panel.");
        var presentation = items.ChatContext?.Presentation ?? throw new AssertionException("Expected a conversation.");
        Assert.That(panel.TryGetViewportSnapshot(out var snapshot), Is.True);
        var turn = presentation.GetUserTurnNode(presentation.Rows[snapshot.AnchorIndex]);
        Assert.That(turn, Is.Not.Null);
        Assert.That(items.ReadingTurnNode, Is.SameAs(turn));
    }
}