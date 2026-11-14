using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Messages;
using LiveMarkdown.Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using Everywhere.ViewModels;
using Everywhere.Views;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShadUI;

namespace Everywhere.Core.Tests.Common.SoftwareUpdate;

[TestFixture]
[NonParallelizable]
public sealed class TerminalUpdatePresentationTests
{
    private static readonly Lazy<ShadTheme> Theme = new(() => new ShadTheme());

    [AvaloniaTest]
    public async Task TerminalUpdate_WhenTargetResolved_DoesNotInvokeGraphicalInstaller()
    {
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.TerminalUpdateHandler.Returns(Substitute.For<ITerminalUpdateHandler>());
        using var model = new ChangeLogViewModel(updater, Substitute.For<IHttpClientFactory>(), NullLogger<ChangeLogViewModel>.Instance);
        var setResolvedAction = typeof(ChangeLogViewModel).GetMethod("SetResolvedAction", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ChangeLogViewModel), "SetResolvedAction");
        var target = CreateTarget();
        setResolvedAction.Invoke(model, [UpdatePageAction.DownloadAndInstall, target]);
        Assert.That(model.UpdateTarget, Is.SameAs(target));
        Assert.That(model.IsUpdateButtonVisible, Is.False);
        await model.PerformUpdateCommand.ExecuteAsync(null);
        await updater.DidNotReceive().PerformUpdateAsync(Arg.Any<SoftwareUpdateMetadata>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task SettingsButton_WhenLinuxUpdateIsAvailable_ShowsConversationWithInlineCopyAndClose()
    {
        var application = Application.Current ?? throw new InvalidOperationException("No application.");
        var previousLocale = LocaleManager.CurrentLocale;
        application.Styles.Add(Theme.Value);
        var markdownStyles = new StyleInclude(new Uri("avares://LiveMarkdown.Avalonia/"))
        {
            Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml")
        };
        application.Styles.Add(markdownStyles);
        var styles = new StyleInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Assets/Styles.axaml")
        };
        application.Styles.Add(styles);
        var appearance = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Everywhere.Core/Assets/Resources.axaml"));
        var evy = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Everywhere.Core/Views/Controls/Evy.axaml"));
        application.Resources.MergedDictionaries.Add(appearance);
        application.Resources.MergedDictionaries.Add(evy);
        var resources = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Everywhere.Core/Views/Configuration/SoftwareUpdateControl.axaml"));
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.TerminalUpdateHandler.Returns(Substitute.For<ITerminalUpdateHandler>());
        var control = new SoftwareUpdateControl(new PersistentState(new InMemoryKeyValueStorage()), updater, Substitute.For<IServiceProvider>())
        {
            Theme = resources[typeof(SoftwareUpdateControl)] as ControlTheme
        };
        var toastHost = new ToastHost();
        var dialogHost = new DialogHost();
        var window = new Window
        {
            Width = 800, Height = 600,
            Content = new Panel
            {
                Children =
                {
                    new Border { Padding = new Thickness(24), Child = control },
                    toastHost,
                    dialogHost
                }
            }
        };
        try
        {
            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var check = control.GetVisualDescendants().OfType<CheckUpdate>().Single();
            Assert.That(check.IsUpdateAvailable, Is.False);
            await control.UpdateOrCheckCommand.ExecuteAsync(null);
            await updater.Received(1).CheckForUpdatesAsync(true, Arg.Any<CancellationToken>());
            updater.DisplayUpdate.Returns(CreateTarget());
            updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.DisplayUpdate)));
            Dispatcher.UIThread.RunJobs();
            Assert.That(check.IsUpdateAvailable, Is.True);
            var available = check.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "UpdateAvailableBorder");
            var title = available.GetVisualDescendants().OfType<TextBlock>().SelectMany(block => block.Inlines ?? []).OfType<Run>().First();
            Assert.That(title.Text, Is.EqualTo(LocaleKey.CommonSettings_SoftwareUpdate_UpdateAvailableTitle_Text.I18N()));
            Assert.That(available.GetVisualDescendants().OfType<Lucide.Avalonia.LucideIcon>().Single().Kind.ToString(), Is.EqualTo("RefreshCw"));
            var clipboard = window.Clipboard ?? throw new InvalidOperationException("No clipboard.");
            await clipboard.SetTextAsync("unchanged-until-copy");
            var updateTask = control.UpdateOrCheckCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var guidance = dialogHost.GetVisualDescendants().OfType<TerminalUpdateGuidance>().Single();
            Assert.That(check.IsCheckingUpdate, Is.False);
            Assert.That(guidance.GetVisualDescendants().OfType<TabControl>(), Is.Empty);
            Assert.That(guidance.MarkdownBuilder.ToString(), Does.Not.Contain(LinkConstants.OfficialWebsiteUri.ToString()));
            var download = guidance.GetVisualDescendants().OfType<HyperlinkButton>().Single();
            Assert.That(download.NavigateUri, Is.EqualTo(LinkConstants.OfficialWebsiteUri));
            Assert.That(download.Content, Is.EqualTo(LocaleKey.TerminalUpdate_Website_OpenDownload.I18N()));
            await WaitForCommandAsync(guidance);
            Assert.That(guidance.MarkdownBuilder.ToString(), Does.Contain(TerminalUpdateGuidance.UpdateCommand));
            Assert.That(guidance.GetVisualDescendants().OfType<MarkdownRenderer>(), Has.Exactly(1).Items);
            Assert.That(guidance.GetVisualDescendants().OfType<Evy>().Single().IsIdleAnimationEnabled, Is.True);
            var code = guidance.GetVisualDescendants().OfType<CodeBlock>().Single();
            Assert.That(code.Code, Is.EqualTo(TerminalUpdateGuidance.UpdateCommand));
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo("unchanged-until-copy"));
            Assert.That(code.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Single(button => button.Name == "PART_ToggleTextWrapButton").IsVisible, Is.False);
            var copyButton = code.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_CopyButton");
            copyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 20 && await clipboard.TryGetTextAsync() != TerminalUpdateGuidance.UpdateCommand; attempt++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo(TerminalUpdateGuidance.UpdateCommand));
            Assert.That(dialogHost.GetVisualDescendants().OfType<TerminalUpdateGuidance>(), Has.Exactly(1).Items);
            Assert.That(guidance.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "CloseButton"), Is.False);
            var cancelButton = guidance.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CancelButton");
            var footerButtons = dialogHost.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Name is "PART_PrimaryButton" or "PART_CancelButton" or "PART_SecondaryButton" or "PART_TertiaryButton")
                .Where(button => button.IsVisible).ToArray();
            Assert.That(footerButtons, Is.Empty);
            Assert.That(cancelButton.Content, Is.EqualTo(LocaleKey.Common_Cancel.I18N()));
            await updater.DidNotReceive().PerformUpdateAsync(Arg.Any<CancellationToken>());

            var chunkLengths = new List<int>();
            void RecordChunk(in ObservableStringBuilderChangedEventArgs change)
            {
                chunkLengths.Add(guidance.MarkdownBuilder.Length);
            }
            guidance.MarkdownBuilder.Changed += RecordChunk;
            LocaleManager.CurrentLocale = LocaleName.En;
            Assert.That(title.Text, Is.EqualTo(LocaleKey.CommonSettings_SoftwareUpdate_UpdateAvailableTitle_Text.I18N()));
            Assert.That(guidance.IsStreaming, Is.True);
            Assert.That(guidance.MarkdownBuilder.ToString(), Does.Not.Contain(TerminalUpdateGuidance.UpdateCommand));
            await WaitForCommandAsync(guidance);
            guidance.MarkdownBuilder.Changed -= RecordChunk;
            Assert.That(chunkLengths.Where(length => length > 0).Distinct().Count(), Is.GreaterThan(1));
            Assert.That(guidance.MarkdownBuilder.ToString(), Does.Contain(LocaleKey.TerminalUpdate_Introduction.I18N()));
            Assert.That(download.Content, Is.EqualTo("official website"));
            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            await clipboard.SetTextAsync("unchanged-on-close");
            cancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            await updateTask;
            Assert.That(await clipboard.TryGetTextAsync(), Is.EqualTo("unchanged-on-close"));
            Assert.That(guidance.IsStreaming, Is.False);
            var stoppedLength = guidance.MarkdownBuilder.Length;
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            Assert.That(dialogHost.Classes.Contains("ChatUpdateGuidance"), Is.False);
            Assert.That(guidance.MarkdownBuilder.Length, Is.EqualTo(stoppedLength));

            if (Environment.GetEnvironmentVariable("EVERYWHERE_UPDATE_PREVIEW_DIR") is { } output)
            {
                Directory.CreateDirectory(output);
                foreach (var locale in new[] { LocaleName.ZhHans, LocaleName.En })
                {
                    foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    {
                        LocaleManager.CurrentLocale = locale;
                        application.RequestedThemeVariant = variant;
                        var previewTask = control.UpdateOrCheckCommand.ExecuteAsync(null);
                        Dispatcher.UIThread.RunJobs();
                        var previewGuidance = dialogHost.GetVisualDescendants().OfType<TerminalUpdateGuidance>().Single();
                        await WaitForCommandAsync(previewGuidance);
                        await Task.Delay(200);
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
                        frame.Save(Path.Combine(output, $"linux-update-dialog-{locale}-{variant.Key.ToString()?.ToLowerInvariant()}.png"), PngBitmapEncoderOptions.Default);
                        var previewCloseButton = dialogHost.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CancelButton");
                        previewCloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        Dispatcher.UIThread.RunJobs();
                        await previewTask;
                    }
                }
            }
        }
        finally
        {
            window.Close();
            application.Styles.Remove(styles);
            application.Styles.Remove(markdownStyles);
            application.Styles.Remove(Theme.Value);
            application.Resources.MergedDictionaries.Remove(appearance);
            application.Resources.MergedDictionaries.Remove(evy);
            application.RequestedThemeVariant = ThemeVariant.Default;
            LocaleManager.CurrentLocale = previousLocale;
        }
    }

    [AvaloniaTest]
    public async Task ChatButton_WhenTerminalManaged_RequestsMainWindowGuidanceWithoutInstalling()
    {
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.TerminalUpdateHandler.Returns(Substitute.For<ITerminalUpdateHandler>());
        var button = new SoftwareUpdateButton { SoftwareUpdater = updater };
        var recipient = new object();
        ApplicationMessage? received = null;
        var hasCloakedChat = false;
        WeakReferenceMessenger.Default.Register<CloakChatWindowMessage>(recipient, (_, message) => hasCloakedChat = message.IsCloaked);
        WeakReferenceMessenger.Default.Register<ApplicationMessage>(recipient, (_, message) => received = message);
        try
        {
            typeof(SoftwareUpdateButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(button, null);
            Assert.That(received, Is.TypeOf<ShowTerminalUpdateGuidanceMessage>());
            Assert.That(hasCloakedChat, Is.True);
            await updater.DidNotReceive().PerformUpdateAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    private static async Task WaitForCommandAsync(TerminalUpdateGuidance guidance)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (!guidance.IsStreaming && guidance.GetVisualDescendants().OfType<CodeBlock>().Any(code => code.Code == TerminalUpdateGuidance.UpdateCommand))
            {
                return;
            }
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Fail("The complete update command was not rendered.");
    }

    private static SoftwareUpdateMetadata CreateTarget()
    {
        return new SoftwareUpdateMetadata(
            UpdateChannel.Canary,
            "v99.0.0-canary.1",
            SemanticVersion.Parse("99.0.0-canary.1") ?? throw new InvalidOperationException("Invalid test version."),
            DateTimeOffset.UtcNow,
            new SoftwareUpdateMetadata.UpdateAsset(
                "Everywhere-Linux-x64-v99.0.0-canary.1.tar.zst",
                "sha256:" + new string('a', 64),
                1024,
                "https://github.com/pasical/Everywhere/releases",
                "https://github.com/pasical/Everywhere/releases"));
    }
}