using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Common;
using Everywhere.I18N;
using Everywhere.ViewModels;
using Everywhere.Views;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShadUI;

namespace Everywhere.Core.Tests.Common.SoftwareUpdate;

[TestFixture]
public sealed class UpdatePresentationTests
{
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
        var window = new Window { Content = button };
        window.Show();
        try
        {
            Assert.That(button.Progress, Is.EqualTo(0.2d));
            window.Content = null;
            SetOperation(updater, SoftwareUpdateOperationStage.Downloading, 0.7d);
            window.Content = button;
            Assert.That(button.Progress, Is.EqualTo(0.7d));
            Assert.That(button.IsBusy, Is.True);
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
            1, SoftwareUpdateOperationIntent.Manual, stage, null, progress));
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.CurrentOperation)));
        Dispatcher.UIThread.RunJobs();
    }
}
