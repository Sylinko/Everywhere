using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;
using Everywhere.Interop;
using NSubstitute;

namespace Everywhere.Core.Tests.Configuration;

public sealed class TextSelectionControllerTests
{
    [TestCase(TextSelectionFeature.AutomaticAttachment)]
    [TestCase(TextSelectionFeature.Toolbar)]
    public async Task SetEnabled_WhenOtherConsumerRemainsEnabled_KeepsMonitoringActive(TextSelectionFeature firstFeature)
    {
        var provider = Substitute.For<IServiceProvider>();
        var dismissWatcher = Substitute.For<IOverlayInputWatcher>();
        dismissWatcher.IsSupported.Returns(true);
        provider.GetService(typeof(IOverlayInputWatcher)).Returns(dismissWatcher);
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
        using var controller = new TextSelectionController(settings, watcher);
        var secondFeature = firstFeature == TextSelectionFeature.Toolbar ? TextSelectionFeature.AutomaticAttachment : TextSelectionFeature.Toolbar;

        await controller.SetEnabledCommand.ExecuteAsync(new(firstFeature, true));
        await controller.SetEnabledCommand.ExecuteAsync(new(secondFeature, true));
        await controller.SetEnabledCommand.ExecuteAsync(new(firstFeature, false));

        Assert.Multiple(() =>
        {
            Assert.That(controller.IsMonitoringRequested, Is.True);
            Assert.That(controller.IsAutomaticAttachmentEnabled, Is.EqualTo(secondFeature == TextSelectionFeature.AutomaticAttachment));
            Assert.That(controller.IsToolbarEnabled, Is.EqualTo(secondFeature == TextSelectionFeature.Toolbar));
            Assert.That(effectiveStates, Is.EqualTo(new[] { true }));
        });

        await controller.SetEnabledCommand.ExecuteAsync(new(secondFeature, false));
        Assert.That(controller.IsMonitoringRequested, Is.False);
        Assert.That(effectiveStates, Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public void SetEnabled_WhenHostRejects_DoesNotCommitIntentOrPermitConcurrentChanges()
    {
        var provider = Substitute.For<IServiceProvider>();
        var dismissWatcher = Substitute.For<IOverlayInputWatcher>();
        dismissWatcher.IsSupported.Returns(true);
        provider.GetService(typeof(IOverlayInputWatcher)).Returns(dismissWatcher);
        var settings = new Settings(provider);
        var watcher = Substitute.For<ITextSelectionWatcher>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.SetEnabledCommand.Returns(new AsyncRelayCommand<bool>(_ => completion.Task));
        using var controller = new TextSelectionController(settings, watcher);

        var operation = controller.SetEnabledCommand.ExecuteAsync(new(TextSelectionFeature.Toolbar, true));
        Assert.Multiple(() =>
        {
            Assert.That(controller.SetEnabledCommand.CanExecute(new(TextSelectionFeature.AutomaticAttachment, true)), Is.False);
            Assert.That(settings.TextSelection.IsToolbarEnabled, Is.False);
        });
        completion.SetException(new InvalidOperationException("Native monitoring rejected."));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await operation);
        Assert.That(controller.IsMonitoringRequested, Is.False);
    }
}