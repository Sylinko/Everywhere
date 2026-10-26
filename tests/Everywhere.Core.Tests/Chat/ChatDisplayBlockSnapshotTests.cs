using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatDisplayBlockSnapshotTests
{
    [AvaloniaTest]
    public async Task Progress_WhenWorkerReports_PreservesContainerAndRealizedControl()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("work", []));
        var assistant = new AssistantChatMessage();
        var tool = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(tool));
        data.Add(assistant);
        var container = new ChatPluginContainerDisplayBlock();
        var reporter = container.DisplaySink.AppendProgress(new DirectLocaleKey("Progress"));
        tool.DisplaySink.AppendBlock(container);
        var presenter = new ChatPluginDisplayBlockPresenter { Content = container };
        var theme = LoadTheme();
        presenter.Theme = theme;
        var window = new Window { Content = presenter, Width = 400, Height = 200 };
        window.Resources[typeof(ChatPluginDisplayBlockPresenter)] = theme;
        try
        {
            window.Show();
            window.UpdateLayout();
            var progress = presenter.GetVisualDescendants().OfType<ProgressBar>().Single();
            var snapshot = presenter.Snapshot as ChatContainerBlockState ?? throw new AssertionException("Missing container snapshot.");
            var membership = snapshot.Children;

            await Task.Run(() =>
            {
                reporter.Report(0.2);
                reporter.Report(0.5);
                reporter.Report(0.8);
            });
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.That(presenter.GetVisualDescendants().OfType<ProgressBar>().Single(), Is.SameAs(progress));
            Assert.That(progress.Value, Is.EqualTo(0.8));
            Assert.That((presenter.Snapshot as ChatContainerBlockState)?.Children, Is.SameAs(membership));
            reporter.Report(double.NaN);
            Dispatcher.UIThread.RunJobs();
            Assert.That(progress.IsIndeterminate, Is.True);

            window.Content = null;
            reporter.Report(0.4);
            window.Content = presenter;
            window.UpdateLayout();
            Assert.That(presenter.GetVisualDescendants().OfType<ProgressBar>().Single().Value, Is.EqualTo(0.4));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task StandaloneProgress_WhenNoChatOwnerExists_RefreshesAndReattaches()
    {
        var block = new ChatPluginProgressDisplayBlock(new DirectLocaleKey("Progress"));
        var presenter = new ChatPluginDisplayBlockPresenter { Content = block, Theme = LoadTheme() };
        var window = new Window { Content = presenter };
        try
        {
            window.Show();
            window.UpdateLayout();
            await Task.Run(() => block.Report(0.6));
            Dispatcher.UIThread.RunJobs();
            Assert.That(presenter.Snapshot, Is.EqualTo(new ChatProgressBlockState(block.HeaderKey, 0.6)));

            window.Content = null;
            block.Report(1);
            Dispatcher.UIThread.RunJobs();
            Assert.That(presenter.Snapshot, Is.EqualTo(new ChatProgressBlockState(block.HeaderKey, 0.6)));
            window.Content = presenter;
            Assert.That(presenter.Snapshot, Is.EqualTo(new ChatProgressBlockState(block.HeaderKey, 1)));
        }
        finally
        {
            window.Close();
        }
    }

    private static ControlTheme LoadTheme()
    {
        var resource = new ResourceInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Views/Chat/ChatPluginDisplayBlockPresenter.axaml")
        };
        return resource.Loaded[typeof(ChatPluginDisplayBlockPresenter)] as ControlTheme ?? throw new AssertionException("Missing block theme.");
    }
}