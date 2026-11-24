using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using Everywhere.Storage;
using Everywhere.Views;
using Tabler.Avalonia;
using ShadUI;

namespace Everywhere.Core.Tests.Chat;

public sealed partial class ChatTurnRenderTests
{
    private static async Task VerifyHistoryOpeningAndSwitchingAsync()
    {
        await using var empty = new RootChatContext(new ChatData(), null);
        using var database = ChatTestDatabase.Create();
        await using var savedFirst = CreateHistory(8);
        await using var savedSecond = CreateHistory(16);
        var writer = new ChatTurnStorage(database.Factory);
        await writer.SaveDataAsync(savedFirst.Data);
        await writer.SaveDataAsync(savedSecond.Data);
        var reader = new ChatTurnStorage(database.Factory);
        await using var first = await reader.LoadContextAsync(savedFirst.Data.Metadata.Id);
        await using var second = await reader.LoadContextAsync(savedSecond.Data.Metadata.Id);
        Assert.That(first.Data.SelectedNodes[1].IsMessageLoaded, Is.False);
        await first.Presentation.PrepareInitialWindowAsync();
        await second.Presentation.PrepareInitialWindowAsync();
        Assert.That(first.Presentation.Rows, Is.Not.Empty);
        Assert.That(second.Presentation.IsAtLatest, Is.True);
        Assert.That(second.Data.SelectedNodes[1].IsMessageLoaded, Is.False);
        Assert.That(second.Presentation.Rows.OfType<ChatMessagePresentationRow>().Count(), Is.EqualTo(ChatPresentation.TurnBatchSize));
        var items = new ChatMessageItemsControl { ChatContext = empty, Margin = new Thickness(12, 8, 12, 0) };
        var scroll = new ScrollViewer { Content = new StackPanel { Children = { items } } };
        var window = CreateWindow(scroll, 720, 620);
        try
        {
            window.Show();
            SettleLayout(window);
            items.ChatContext = first;
            SettleLayout(window);
            AssertAtBottom(scroll);
            AssertReadingTurn(items);

            scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y - 200);
            SettleLayout(window);
            Assert.That(scroll.Offset.Y, Is.LessThan(scroll.ScrollBarMaximum.Y - 100));

            items.ChatContext = second;
            // Inspect the first layout, before Loaded/Background callbacks can conceal a flash.
            window.UpdateLayout();
            AssertAtBottom(scroll);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            AssertAtBottom(scroll);
            SettleLayout(window);
            AssertAtBottom(scroll);
            AssertReadingTurn(items);
            Assert.That(second.Presentation.HasEarlierTurns, Is.True);
            SaveRender(window, "chatturn-history-bottom.png");
        }
        finally
        {
            window.Close();
        }
    }

    private static RootChatContext CreateHistory(int turnCount)
    {
        var context = new RootChatContext(new ChatData(), null);
        for (var index = 0; index < turnCount; index++)
        {
            context.Data.StartTurn(new UserChatMessage($"History question {index}", []));
            var reply = new AssistantChatMessage();
            reply.AddSpan(new AssistantChatMessageTextSpan(
                $"## Saved answer {index}\n\n" + string.Join("\n\n", Enumerable.Repeat("A saved paragraph with enough text to exercise variable-height history rows.", index % 3 + 1))));
            context.Data.Append(reply);
        }

        return context;
    }

    private static void SettleLayout(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertAtBottom(ScrollViewer scroll)
    {
        Assert.That(scroll.ScrollBarMaximum.Y, Is.GreaterThan(0));
        Assert.That(scroll.Offset.Y, Is.EqualTo(scroll.ScrollBarMaximum.Y).Within(0.5));
    }

    [AvaloniaTest]
    public async Task Timeline_WhenStreamingAndSwitchingWindows_RendersSnapshotsAndKeepsVirtualizedContainers()
    {
        await using var context = new RootChatContext(new ChatData(), null);
        var user = context.Data.StartTurn(new UserChatMessage("Review the local history upgrade and verify the migration.", []));
        var assistant = new AssistantChatMessage { IsBusy = true };
        var text = new AssistantChatMessageTextSpan("## Local history upgrade\n\nI will check the **turn structure**, then verify the saved branches.\n");
        assistant.AddSpan(text);
        var tool = new FunctionCallChatMessage(TablerIconKind.Database, new DirectLocaleKey("Verify migration")) { IsBusy = true };
        var progress = tool.DisplaySink.AppendProgress(new DirectLocaleKey("Checking saved branches"));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(tool));
        context.Data.Append(assistant);
        var presentation = context.Presentation;
        await presentation.PrepareInitialWindowAsync();
        var textRow = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        var group = presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        group.IsExpanded = true;
        group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single().IsExpanded = true;
        var items = new ChatMessageItemsControl { ChatContext = context, Margin = new Thickness(20) };
        var scroll = new ScrollViewer { Content = items };
        var window = CreateWindow(scroll, 720, 620);
        try
        {
            window.Show();
            window.UpdateLayout();
            await context.DispatchAsync(() =>
            {
                progress.Report(0.7);
                text.ContentMarkdownBuilder.Append("\n- Existing choices are preserved.\n- Archived chats stay local.\n");
            });
            await WaitForMarkdownAsync(textRow, text.ContentMarkdownBuilder.Version);
            window.UpdateLayout();
            Assert.That(presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single(), Is.SameAs(textRow));
            SaveRender(window, "chatturn-streaming.png");
            Assert.That(items.GetVisualDescendants().OfType<ProgressBar>().Any(bar => bar.Value == 0.7), Is.True);

            var expander = items.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "PART_GroupExpander");
            expander.IsExpanded = false;
            await context.DispatchAsync(() => progress.Report(0.9));
            SettleLayout(window);
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Rows.OfType<ActivityGroupPresentationRow>().Single(), Is.SameAs(group));
                Assert.That(group.IsExpanded, Is.False);
                Assert.That(expander.IsExpanded, Is.False);
            });
            SaveRender(window, "chatturn-tools-collapsed.png");
            expander.IsExpanded = true;
            group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single().IsExpanded = true;
            SettleLayout(window);
            Assert.That(items.GetVisualDescendants().OfType<ProgressBar>().Any(bar => bar.Value == 0.9), Is.True);

            tool.FinishedAt = DateTimeOffset.UtcNow;
            tool.IsBusy = false;
            assistant.FinishedAt = DateTimeOffset.UtcNow;
            assistant.IsBusy = false;
            var alternateMessage = new AssistantChatMessage();
            alternateMessage.AddSpan(new AssistantChatMessageTextSpan("This is the **retried response** on the same turn."));
            var alternate = context.Data.Append(user, alternateMessage, shouldSelectChild: false);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var selector = items.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.IsVisible && control.Maximum == 2);
            selector.Value = 2;
            await context.DispatchAsync(() => { });
            Dispatcher.UIThread.RunJobs();
            await presentation.PrepareInitialWindowAsync();
            window.UpdateLayout();
            Assert.That(context.Data.SelectedNodes[^1], Is.SameAs(alternate));
            var alternateRow = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
            await WaitForMarkdownAsync(alternateRow, 0);
            SaveRender(window, "chatturn-branch.png");
            selector = items.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.IsVisible && control.Maximum == 2);
            selector.Value = 1;
            await context.DispatchAsync(() => { });
            Dispatcher.UIThread.RunJobs();
            await presentation.PrepareInitialWindowAsync();
            Assert.That(context.Data.SelectedNodes[^1].Message, Is.SameAs(assistant));

            for (var index = 0; index < ChatPresentation.TurnBatchSize + 8; index++)
            {
                context.Data.StartTurn(new UserChatMessage($"History question {index}", []));
                var reply = new AssistantChatMessage();
                reply.AddSpan(new AssistantChatMessageTextSpan(
                    $"## Saved answer {index}\n\nThis turn keeps its own message identities and saved choices.\n\n" +
                    "- Read complete turns only when needed.\n- Keep older branches available.\n- Save changed nodes incrementally.\n"));
                context.Data.Append(reply);
            }

            Dispatcher.UIThread.RunJobs();
            await presentation.ShowLatestAsync();
            foreach (var row in presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToArray())
            {
                await WaitForMarkdownAsync(row, 0);
            }

            window.UpdateLayout();
            scroll.ScrollToEnd();
            window.UpdateLayout();
            Assert.That(items.GetVisualDescendants().OfType<VariableHeightVirtualizingStackPanel>().Any(), Is.True);
            Assert.That(items.GetVisualDescendants().OfType<ChatPresentationRowPresenter>().Count(), Is.LessThan(presentation.Rows.Count));
            SaveRender(window, "chatturn-windowed.png");

            var revealed = await presentation.RevealAsync(user, null);
            Assert.That(revealed, Is.Not.Null);
            window.UpdateLayout();
            scroll.ScrollToHome();
            window.UpdateLayout();
            Assert.That(presentation.HasLaterTurns, Is.True);
        }
        finally
        {
            window.Close();
        }

        await VerifyUpgradeNoticeAsync();
        await VerifyHistoryOpeningAndSwitchingAsync();
        // ShadUI owns process-wide theme resources; keep real-template scenarios in one app lifetime.
        await Timeline_WhenFailedToolHasNoOtherDetails_ShowsLocalizedError();
        await Navigation_WhenTurnJumpSupersedesLatest_KeepsNewestTargetAndStableRows();
        await Navigation_WhenEdgeLoadIsSuperseded_DoesNotRestoreOldDirection();
        await SearchNavigation_WhenSurfaceUpdatesAfterUserScroll_DoesNotRecenter();
        await SearchNavigation_WhenResultsArriveAfterNewerIntent_DoesNotTakeBackViewport();
        await HiddenWindow_WhenCompacted_RestoresVisibleReadingAnchor();
        await MinimizedWindow_WhenCompacted_RestoresVisibleReadingAnchor();
        await HiddenWindow_WhenNavigationIsPending_ResumesTargetInsteadOfOldAnchor();
        await HiddenWindow_WhenReadingBranchIsRemoved_FallsBackToLatestAndKeepsNavigationUsable();
        await Navigation_WhenUserScrollsDuringPreparation_CancelsTheJump();
        await HiddenWindow_WhenTailStreamsOrContextChanges_ResumesOnlyTheCurrentTail();
    }

    private static async Task Timeline_WhenFailedToolHasNoOtherDetails_ShowsLocalizedError()
    {
        var previousLocale = LocaleManager.CurrentLocale;
        LocaleManager.CurrentLocale = LocaleName.En;
        try
        {
            await using var context = new RootChatContext(new ChatData(), null);
            context.Data.StartTurn(new UserChatMessage("Inspect the attached image.", []));
            var tool = new FunctionCallChatMessage(
                TablerIconKind.Photo,
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_Header))
            {
                ErrorMessageKey = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_PathCount)
            };
            var assistant = new AssistantChatMessage { IsBusy = true };
            assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(tool));
            context.Data.Append(assistant);
            await context.Presentation.PrepareInitialWindowAsync();
            var group = context.Presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
            group.IsExpanded = true;
            var row = group.Items.OfType<FunctionCallActivityItemPresentationRow>().Single();

            // Argument rejection can leave a tool with only an error, without any display blocks or image results.
            using (Assert.EnterMultipleScope())
            {
                Assert.That(row.State.DisplayBlocks, Is.Empty);
                Assert.That(row.State.ImageViews, Is.Empty);
                Assert.That(row.State.HasDetails, Is.True);
            }

            var items = new ChatMessageItemsControl { ChatContext = context, Margin = new Thickness(20) };
            var window = CreateWindow(new ScrollViewer { Content = items }, 720, 340);
            try
            {
                window.Show();
                SettleLayout(window);
                var marker = items.GetVisualDescendants().OfType<ActivityMarker>().Single(control => ReferenceEquals(control.DataContext, row));
                var englishMessage = LocaleKey.BuiltInChatPlugin_Essential_ViewImage_PathCount.I18N();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(marker.IsExpandable, Is.True);
                    Assert.That(marker.IsExpanded, Is.False);
                    Assert.That(marker.GetVisualDescendants().OfType<SelectableTextBlock>(), Is.Empty);
                }

                var toggle = marker.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "PART_ToggleButton");
                var point = toggle.TranslatePoint(new Point(40, toggle.Bounds.Height / 2), window)
                    ?? throw new AssertionException("The tool toggle is outside the window.");
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                SettleLayout(window);
                var errorText = marker.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(marker.IsExpanded, Is.True);
                    Assert.That(errorText.IsEffectivelyVisible, Is.True);
                    Assert.That(errorText.Text, Is.EqualTo(englishMessage));
                }

                LocaleManager.CurrentLocale = LocaleName.ZhHans;
                SettleLayout(window);
                var localizedMessage = LocaleKey.BuiltInChatPlugin_Essential_ViewImage_PathCount.I18N();
                Assert.That(localizedMessage, Is.Not.EqualTo(englishMessage));
                Assert.That(errorText.Text, Is.EqualTo(localizedMessage));
                await Task.Delay(300);
                SettleLayout(window);
                SaveRender(window, "chatturn-tool-error.png");

                await context.DispatchAsync(() => tool.ErrorMessageKey = null);
                SettleLayout(window);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(marker.IsExpandable, Is.False);
                    Assert.That(errorText.IsEffectivelyVisible, Is.False);
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            LocaleManager.CurrentLocale = previousLocale;
        }
    }

    private static async Task VerifyUpgradeNoticeAsync()
    {
        var notice = new ChatUpgradeNoticeView();
        var host = new DialogHost();
        var window = CreateWindow(host, 600, 420);
        try
        {
            window.Show();
            window.UpdateLayout();
            var completion = DialogManager.CreateDialog(notice, LocaleKey.ChatUpgrade_Title.I18N(), window)
                .WithPrimaryButton(LocaleKey.ChatUpgrade_Continue.I18N())
                .ShowAsync();
            window.UpdateLayout();
            var checkbox = notice.GetVisualDescendants().OfType<CheckBox>().Single();
            Assert.That(checkbox.IsChecked, Is.False);
            Assert.That(notice.ShouldTrashLegacyDatabase, Is.False);
            if (Environment.GetEnvironmentVariable("EVERYWHERE_RENDER_OUTPUT") is not null)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(200);
            }

            SaveRender(window, "chatturn-upgrade.png");
            checkbox.IsChecked = true;
            Assert.That(notice.ShouldTrashLegacyDatabase, Is.True);
            var button = host.GetVisualDescendants().OfType<Button>().Single(candidate => Equals(candidate.Content, LocaleKey.ChatUpgrade_Continue.I18N()));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(await completion.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(DialogResult.Primary));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ActivityMarker_WhenItemsControlIsReadonly_DisablesNestedCancelButton()
    {
        var user = new UserChatMessage("Work", []);
        var assistant = new AssistantChatMessage { IsBusy = true };
        await using var context = new RootChatContext(new ChatData(), null);
        context.Data.StartTurn(user);
        context.Data.Append(assistant);
        context.Data.StartActivity(
            TablerIconKind.Mcp,
            new DirectLocaleKey("Starting MCP"),
            removeAfterCompletion: false,
            owner: assistant,
            cancelAction: static () => { });
        Dispatcher.UIThread.RunJobs();
        await context.Presentation.PrepareInitialWindowAsync();

        var group = context.Presentation.Rows.OfType<ActivityGroupPresentationRow>().Single();
        group.IsExpanded = true;
        var row = group.Items.OfType<BusyActivityItemPresentationRow>().Single();

        var items = new ChatMessageItemsControl
        {
            ChatContext = context,
            Margin = new Thickness(20)
        };
        var window = CreateWindow(new ScrollViewer { Content = items }, 720, 340);
        try
        {
            window.Show();
            SettleLayout(window);

            var marker = items.GetVisualDescendants().OfType<ActivityMarker>().Single(control => ReferenceEquals(control.DataContext, row));
            var cancelButton = marker.GetVisualDescendants().OfType<Button>().Single(static button => button.Name == "PART_CancelButton");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(marker.IsCancelEnabled, Is.True);
                Assert.That(marker.CanCancel, Is.True);
                Assert.That(cancelButton.IsVisible, Is.True);
            }

            items.IsReadonly = true;
            SettleLayout(window);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(marker.IsCancelEnabled, Is.False);
                Assert.That(marker.CanCancel, Is.False);
                Assert.That(cancelButton.IsVisible, Is.False);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateWindow(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height };
        NameScope.SetNameScope(window, new NameScope());
        var application = Application.Current ?? throw new InvalidOperationException("The test application is not initialized.");
        if (!application.Styles.OfType<ShadTheme>().Any())
        {
            application.Styles.Add(new ShadTheme());
        }

        window.Styles.Add(new StyleInclude(new Uri("avares://Everywhere.Core/")) { Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml") });
        window.Styles.Add(new StyleInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Views/Chat/ChatMessageOperationPanel.axaml")
        });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Views/Controls/ConditionalContentControl.axaml")
        });
        foreach (var name in new[]
                 {
                     "ChatMessageControl", "ChatMessageItemsControl", "ChatPresentationRowPresenter", "ActivityMarker",
                     "ChatActivityPreviewPresenter", "ChatPluginDisplayBlockPresenter", "ChatAttachmentItemsControl",
                     "ChatActionBubble", "TerminalView"
                 })
        {
            window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Everywhere.Core/"))
            {
                Source = new Uri($"avares://Everywhere.Core/Views/Chat/{name}.axaml")
            });
        }

        window.Resources["FontSizeM"] = 14d;
        window.Resources["FontSizeS"] = 12.8d;
        window.Resources["FontSizeXs"] = 10d;
        window.Resources["FontSizeL"] = 16d;
        window.Content = content;
        return window;
    }

    private static async Task WaitForMarkdownAsync(AssistantTextOutputPresentationRow row, long version)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (row.Markdown.DocumentUpdate is { } update && (version == 0 || update.Version == version))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The latest Markdown snapshot was not presented.");
    }

    private static void SaveRender(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("EVERYWHERE_RENDER_OUTPUT") is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var bitmap = window.CaptureRenderedFrame() ?? throw new AssertionException("The window did not produce a rendered frame.");
        bitmap.Save(Path.Combine(directory, name), new PngBitmapEncoderOptions());
    }
}