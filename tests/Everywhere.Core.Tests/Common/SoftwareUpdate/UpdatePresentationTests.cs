using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Common;
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
public sealed class UpdatePresentationTests
{
    [AvaloniaTest]
    [TestCase(UpdatePageAction.DownloadAndInstall)]
    [TestCase(UpdatePageAction.Install)]
    [TestCase(UpdatePageAction.InstallFallback)]
    public void ChangeLogButton_WhenLanguageTargetOrActionChanges_UpdatesLiveContent(UpdatePageAction action)
    {
        var oldLocale = LocaleManager.CurrentLocale;
        LocaleManager.CurrentLocale = LocaleName.En;
        try
        {
            var updater = Substitute.For<ISoftwareUpdater>();
            using var viewModel = new ChangeLogViewModel(
                updater, Substitute.For<IHttpClientFactory>(), NullLogger<ChangeLogViewModel>.Instance);
            var button = new Button();
            using var binding = button.Bind(ContentControl.ContentProperty, new Binding("UpdateButtonContentKey^") { Source = viewModel });
            var target = CreateTarget("99.0.0");
            if (action == UpdatePageAction.Install) updater.ReadyUpdate.Returns(target);
            SetResolvedAction(viewModel, action, target);
            Assert.That(viewModel.UpdateAction, Is.EqualTo(action));
            var englishContent = button.Content;
            Assert.That(englishContent, Does.Contain("99.0.0"));

            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            Assert.That(button.Content, Is.Not.EqualTo(englishContent));
            Assert.That(button.Content, Does.Contain("99.0.0"));

            target = CreateTarget("99.0.1");
            if (action == UpdatePageAction.Install) updater.ReadyUpdate.Returns(target);
            SetResolvedAction(viewModel, action, target);
            Assert.That(viewModel.UpdateAction, Is.EqualTo(action));
            Assert.That(button.Content, Does.Contain("99.0.1"));
            Assert.That(button.Content, Does.Not.Contain("99.0.0"));

            SetResolvedAction(viewModel, UpdatePageAction.Retry, null);
            Assert.That(button.Content, Is.EqualTo(LocaleKey.Common_Retry.I18N()));
            SetOperation(updater, SoftwareUpdateOperationStage.Downloading, 0.2d);
            Assert.That(button.Content, Is.EqualTo(LocaleKey.Common_Cancel.I18N()));
            LocaleManager.CurrentLocale = LocaleName.En;
            Assert.That(button.Content, Is.EqualTo(LocaleKey.Common_Cancel.I18N()));
        }
        finally
        {
            LocaleManager.CurrentLocale = oldLocale;
        }
    }

    [AvaloniaTest]
    public void ChatButton_WhenFailureStateChanges_UpdatesContentTemplateErrorDot()
    {
        var application = Application.Current ?? throw new InvalidOperationException("No Avalonia application.");
        var resources = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://ShadUI/Controls/Resources.axaml"));
        application.Resources.MergedDictionaries.Add(resources);
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.IsUpdateActionVisible.Returns(true);
        var button = new SoftwareUpdateButton { SoftwareUpdater = updater, Width = 32, Height = 32 };
        var window = new Window { Content = button };
        try
        {
            var themeResources = (ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri("avares://Everywhere.Core/Views/Chat/SoftwareUpdateButton.axaml"));
            button.Theme = themeResources[typeof(SoftwareUpdateButton)] as ControlTheme ??
                throw new InvalidOperationException("No software update button theme.");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var errorDot = button.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ErrorDot");
            Assert.That(errorDot.IsVisible, Is.False);

            SetOperation(updater, SoftwareUpdateOperationStage.Failed, 0d);
            Assert.That(errorDot.IsVisible, Is.True);

            foreach (var stage in new[] { SoftwareUpdateOperationStage.Checking, SoftwareUpdateOperationStage.Downloading,
                         SoftwareUpdateOperationStage.Canceled, SoftwareUpdateOperationStage.Completed })
            {
                SetOperation(updater, stage, 0d);
                Assert.That(errorDot.IsVisible, Is.False);
            }
        }
        finally
        {
            window.Close();
            application.Resources.MergedDictionaries.Remove(resources);
        }
    }

    [AvaloniaTest]
    public async Task ChatButton_WhenManualUpdateFails_ShowsOneErrorToast()
    {
        if (Application.Current is { } application)
        {
            application.Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri("avares://ShadUI/Controls/Resources.axaml")));
        }
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.IsUpdateActionVisible.Returns(true);
        updater.PerformUpdateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("Download failed")));
        var button = new SoftwareUpdateButton { SoftwareUpdater = updater };
        var window = new Window { Content = new Grid { Children = { button, new ToastHost() } } };
        window.Show();
        try
        {
            typeof(SoftwareUpdateButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(button, null);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.GetVisualDescendants().OfType<Toast>().Count(), Is.EqualTo(1));
            Assert.That(updater.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ISoftwareUpdater.PerformUpdateAsync)), Is.EqualTo(1));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Operation_WhenStagesChange_ProjectsProgressAndCancellationWithoutToasts()
    {
        var updater = Substitute.For<ISoftwareUpdater>();
        using var viewModel = new ChangeLogViewModel(
            updater, Substitute.For<IHttpClientFactory>(), NullLogger<ChangeLogViewModel>.Instance);

        SetOperation(updater, SoftwareUpdateOperationStage.Downloading, 0.42d);
        Assert.That(viewModel.IsUpdateInProgress, Is.True);
        Assert.That(viewModel.IsUpdateProgressIndeterminate, Is.False);
        Assert.That(viewModel.UpdateProgress, Is.EqualTo(0.42d));
        Assert.That(viewModel.UpdateAction, Is.EqualTo(UpdatePageAction.Cancel));
        await viewModel.PerformUpdateCommand.ExecuteAsync(null);
        updater.Received(1).CancelCurrentOperation();

        foreach (var stage in new[] { SoftwareUpdateOperationStage.Validating, SoftwareUpdateOperationStage.Cancelling, SoftwareUpdateOperationStage.Installing })
        {
            SetOperation(updater, stage, double.NaN);
            Assert.That(viewModel.IsUpdateInProgress, Is.True);
            Assert.That(viewModel.IsUpdateProgressIndeterminate, Is.True);
            Assert.That(viewModel.UpdateProgress, Is.Zero);
            Assert.That(viewModel.UpdateStatusKey, Is.Not.SameAs(DirectLocaleKey.Empty));
            Assert.That(viewModel.IsUpdateButtonVisible, Is.EqualTo(stage == SoftwareUpdateOperationStage.Validating));
        }

        SetOperation(updater, SoftwareUpdateOperationStage.Canceled, 0d);
        Assert.That(viewModel.IsUpdateInProgress, Is.False);
        Assert.That(viewModel.UpdateStatusKey, Is.SameAs(DirectLocaleKey.Empty));
    }

    [AvaloniaTest]
    public void ChatButton_WhenReattached_ReadsCurrentProgressWithoutCancellingWork()
    {
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.IsUpdateActionVisible.Returns(true);
        SetOperation(updater, SoftwareUpdateOperationStage.Downloading, 0.2d);
        var button = new SoftwareUpdateButton { SoftwareUpdater = updater };
        var themeResources = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Everywhere.Core/Views/Chat/SoftwareUpdateButton.axaml"));
        button.Theme = themeResources[typeof(SoftwareUpdateButton)] as ControlTheme ??
            throw new InvalidOperationException("No software update button theme.");
        var window = new Window { Content = button };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var ring = button.GetVisualDescendants().OfType<ProgressRing>().Single();
            Assert.That(ring.Value, Is.EqualTo(0.2d));
            window.Content = null;
            SetOperation(updater, SoftwareUpdateOperationStage.Downloading, 0.7d);
            window.Content = button;
            Dispatcher.UIThread.RunJobs();
            ring = button.GetVisualDescendants().OfType<ProgressRing>().Single();
            Assert.That(ring.Value, Is.EqualTo(0.7d));
            Assert.That(ring.IsVisible, Is.True);
            updater.DidNotReceive().CancelCurrentOperation();
        }
        finally
        {
            window.Close();
        }
    }

    private static void SetOperation(ISoftwareUpdater updater, SoftwareUpdateOperationStage stage, double progress)
    {
        updater.CurrentOperation.Returns(new SoftwareUpdateOperation(
            stage, null, progress));
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.CurrentOperation)));
        Dispatcher.UIThread.RunJobs();
    }

    private static SoftwareUpdateMetadata CreateTarget(string versionString)
    {
        if (!SemanticVersion.TryParse(versionString, out var version)) throw new InvalidOperationException("Invalid test version.");
        return new SoftwareUpdateMetadata(UpdateChannel.Stable, $"v{versionString}", version, DateTimeOffset.UtcNow.AddDays(-1),
            new SoftwareUpdateMetadata.UpdateAsset($"update-{versionString}.exe", string.Empty, 3,
                "https://example.invalid/proxy", "https://example.invalid/direct"));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetResolvedAction")]
    private static extern void SetResolvedAction(ChangeLogViewModel viewModel, UpdatePageAction action, SoftwareUpdateMetadata? target);
}