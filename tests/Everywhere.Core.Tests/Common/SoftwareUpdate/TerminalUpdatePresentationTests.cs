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
        typeof(ChangeLogViewModel).GetMethod("SetResolvedAction", BindingFlags.Instance | BindingFlags.NonPublic)?
            .Invoke(model, [UpdatePageAction.DownloadAndInstall, CreateTarget()]);
        Assert.That(model.IsUpdateButtonVisible, Is.False);
        await model.PerformUpdateCommand.ExecuteAsync(null);
        await updater.DidNotReceive().PerformUpdateAsync(Arg.Any<SoftwareUpdateMetadata>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task SettingsButton_WhenLinuxUpdateIsAvailable_CopiesPlainCommandAndRendersExistingControl()
    {
        var application = Application.Current ?? throw new InvalidOperationException("No application.");
        var previousLocale = LocaleManager.CurrentLocale;
        application.Styles.Add(Theme.Value);
        var resources = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://Everywhere.Core/Views/Configuration/SoftwareUpdateControl.axaml"));
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.TerminalUpdateHandler.Returns(Substitute.For<ITerminalUpdateHandler>());
        var control = new SoftwareUpdateControl(new PersistentState(new InMemoryKeyValueStorage()), updater, Substitute.For<IServiceProvider>())
        {
            Theme = resources[typeof(SoftwareUpdateControl)] as ControlTheme
        };
        var toastHost = new ToastHost();
        var window = new Window
        {
            Width = 560, Height = 260,
            Content = new Panel
            {
                Children = { new Border { Padding = new Thickness(24), Child = control }, toastHost }
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
            Assert.That(title.Text, Is.EqualTo(LocaleKey.TerminalUpdate_CopyCommand.I18N()));
            Assert.That(available.GetVisualDescendants().OfType<Lucide.Avalonia.LucideIcon>().Single().Kind.ToString(), Is.EqualTo("Terminal"));
            await control.UpdateOrCheckCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(toastHost.GetVisualDescendants().OfType<Toast>().Any(toast =>
                Equals(toast.Content, LocaleKey.TerminalUpdate_PasteHint.I18N())), Is.True);
            Assert.That(await (window.Clipboard ?? throw new InvalidOperationException("No clipboard.")).TryGetTextAsync(), Is.EqualTo("ew update"));
            await updater.DidNotReceive().PerformUpdateAsync(Arg.Any<CancellationToken>());
            LocaleManager.CurrentLocale = LocaleName.En;
            Assert.That(title.Text, Is.EqualTo(LocaleKey.TerminalUpdate_CopyCommand.I18N()));
            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            if (Environment.GetEnvironmentVariable("EVERYWHERE_UPDATE_PREVIEW_DIR") is { } output)
            {
                Directory.CreateDirectory(output);
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = variant;
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(400);
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
                    frame.Save(Path.Combine(output, $"linux-update-button-{variant.Key.ToString()?.ToLowerInvariant()}.png"), PngBitmapEncoderOptions.Default);
                }
            }
        }
        finally
        {
            window.Close();
            application.Styles.Remove(Theme.Value);
            application.RequestedThemeVariant = ThemeVariant.Default;
            LocaleManager.CurrentLocale = previousLocale;
        }
    }

    private static SoftwareUpdateMetadata CreateTarget() => new(
        UpdateChannel.Canary, "v99.0.0-canary.1", SemanticVersion.Parse("99.0.0-canary.1") ?? throw new InvalidOperationException("Invalid test version."), DateTimeOffset.UtcNow,
        new SoftwareUpdateMetadata.UpdateAsset("Everywhere-Linux-x64-v99.0.0-canary.1.tar.zst", "sha256:" + new string('a', 64), 1024,
            "https://github.com/pasical/Everywhere/releases", "https://github.com/pasical/Everywhere/releases"));
}