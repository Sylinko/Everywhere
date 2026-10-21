using Avalonia.Headless.NUnit;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.TextSelection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

public sealed class TextSelectionControllerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SetEnabled_WhenOtherConsumerRemainsEnabled_KeepsMonitoringActive(bool isToolbarFirst)
    {
        var provider = Substitute.For<IServiceProvider>();
        var settings = new Settings(provider);
        var watcher = Substitute.For<ITextSelectionWatcher>();
        var effectiveStates = new List<bool>();
        var isMonitoringEnabled = false;
        watcher.IsEnabled.Returns(_ => isMonitoringEnabled);
        watcher.SetEnabledCommand.Returns(new AsyncRelayCommand<bool>(isEnabled =>
        {
            effectiveStates.Add(isEnabled);
            isMonitoringEnabled = isEnabled;
            return Task.CompletedTask;
        }));
        using var controller = new TextSelectionController(settings, watcher, NullLogger<TextSelectionController>.Instance);
        var firstCommand = isToolbarFirst ? controller.SetToolbarEnabledCommand : controller.SetAutomaticAttachmentEnabledCommand;
        var secondCommand = isToolbarFirst ? controller.SetAutomaticAttachmentEnabledCommand : controller.SetToolbarEnabledCommand;

        await firstCommand.ExecuteAsync(true);
        await secondCommand.ExecuteAsync(true);
        await firstCommand.ExecuteAsync(false);

        Assert.Multiple(() =>
        {
            Assert.That(controller.IsMonitoringRequested, Is.True);
            Assert.That(controller.IsAutomaticAttachmentEnabled, Is.EqualTo(isToolbarFirst));
            Assert.That(controller.IsToolbarEnabled, Is.EqualTo(!isToolbarFirst));
            Assert.That(effectiveStates, Is.EqualTo(new[] { true }));
        });

        await secondCommand.ExecuteAsync(false);
        Assert.That(controller.IsMonitoringRequested, Is.False);
        Assert.That(effectiveStates, Is.EqualTo(new[] { true, false }));
    }

    [AvaloniaTest]
    public async Task SetEnabled_WhenHostRejects_DoesNotCommitIntentOrPermitConcurrentChanges()
    {
        var provider = Substitute.For<IServiceProvider>();
        var settings = new Settings(provider);
        var watcher = Substitute.For<ITextSelectionWatcher>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.SetEnabledCommand.Returns(new AsyncRelayCommand<bool>(_ => completion.Task));
        using var controller = new TextSelectionController(settings, watcher, NullLogger<TextSelectionController>.Instance);

        var operation = controller.SetToolbarEnabledCommand.ExecuteAsync(true);
        Assert.Multiple(() =>
        {
            Assert.That(controller.IsBusy, Is.True);
            Assert.That(controller.SetAutomaticAttachmentEnabledCommand.CanExecute(true), Is.False);
            Assert.That(settings.TextSelection.IsToolbarEnabled, Is.False);
        });
        completion.SetException(new InvalidOperationException("Native monitoring rejected."));
        await operation;
        Assert.That(controller.IsMonitoringRequested, Is.False);
        Assert.That(controller.IsBusy, Is.False);
        Assert.That(controller.SetAutomaticAttachmentEnabledCommand.CanExecute(true), Is.True);
    }
}