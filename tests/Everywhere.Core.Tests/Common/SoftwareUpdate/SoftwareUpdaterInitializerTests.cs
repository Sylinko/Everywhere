using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ShadUI;

namespace Everywhere.Core.Tests.Common.SoftwareUpdate;

[TestFixture]
public sealed class SoftwareUpdaterInitializerTests
{
    [AvaloniaTest]
    public async Task Notification_WhenOnlyProgressChanges_DoesNotRepublish()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var updater = Substitute.For<ISoftwareUpdater>();
        var publisher = Substitute.For<INotificationPublisher<SoftwareUpdater>>();
        using var initializer = new SoftwareUpdaterInitializer(updater, new Settings(services),
            new PersistentState(new InMemoryKeyValueStorage()), publisher, services);
        await ((IAsyncInitializer)initializer).InitializeAsync(CancellationToken.None);
        var target = new SoftwareUpdateMetadata(UpdateChannel.Stable, "v99.0.0", new SemanticVersion(99), DateTimeOffset.UtcNow.AddDays(-1),
            new SoftwareUpdateMetadata.UpdateAsset("update.exe", string.Empty, 3, "https://example.invalid/proxy", "https://example.invalid/direct"));
        updater.DisplayUpdate.Returns(target);
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.DisplayUpdate)));
        var calls = publisher.ReceivedCalls().Count();
        SetOperation(updater, new SoftwareUpdateOperation(SoftwareUpdateOperationStage.Downloading, target, 0.2d));
        SetOperation(updater, new SoftwareUpdateOperation(SoftwareUpdateOperationStage.Downloading, target, 0.7d));
        Assert.That(publisher.ReceivedCalls().Count(), Is.EqualTo(calls));
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.DisplayUpdate)));
        Assert.That(publisher.ReceivedCalls().Count(), Is.EqualTo(calls + 1));
    }

    [AvaloniaTest]
    public async Task AutomaticChecking_WhenStartedAndReenabled_UsesFourHourIntervalAndApplicationLifetime()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var applicationLifetime = new CancellationTokenSource();
        var settings = new Settings(services);
        settings.Common.IsAutomaticUpdateCheckEnabled = true;
        var updater = Substitute.For<ISoftwareUpdater>();
        var runTokens = new List<CancellationToken>();
        updater.When(x => x.RunAutomaticCheckInBackground(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(call => runTokens.Add(call.Arg<CancellationToken>()));
        using var initializer = new SoftwareUpdaterInitializer(
            updater, settings, new PersistentState(new InMemoryKeyValueStorage()),
            Substitute.For<INotificationPublisher<SoftwareUpdater>>(), services);

        await ((IAsyncInitializer)initializer).InitializeAsync(applicationLifetime.Token);
        updater.Received(1).RunAutomaticCheckInBackground(TimeSpan.FromHours(4), Arg.Any<CancellationToken>());

        settings.Common.IsAutomaticUpdateCheckEnabled = false;
        Assert.That(runTokens[0].IsCancellationRequested, Is.True);
        settings.Common.IsAutomaticUpdateCheckEnabled = true;
        updater.Received(2).RunAutomaticCheckInBackground(TimeSpan.FromHours(4), Arg.Any<CancellationToken>());
        Assert.That(runTokens[1].IsCancellationRequested, Is.False);

        applicationLifetime.Cancel();
        Assert.That(runTokens[1].IsCancellationRequested, Is.True);
        initializer.Dispose();
        settings.Common.IsAutomaticUpdateCheckEnabled = false;
        settings.Common.IsAutomaticUpdateCheckEnabled = true;
        updater.Received(2).RunAutomaticCheckInBackground(TimeSpan.FromHours(4), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task LastCheckTime_WhenDiscoverySucceeds_UpdatesPersistentStateWithoutResettingItAtStartup()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var storage = new InMemoryKeyValueStorage();
        var state = new PersistentState(storage);
        var previous = DateTimeOffset.UtcNow.AddDays(-1);
        state.LastUpdateCheckTime = previous;
        var updater = Substitute.For<ISoftwareUpdater>();
        updater.LastCheckTime.Returns((DateTimeOffset?)null);
        using var initializer = new SoftwareUpdaterInitializer(
            updater, new Settings(services), state,
            Substitute.For<INotificationPublisher<SoftwareUpdater>>(), services);
        await ((IAsyncInitializer)initializer).InitializeAsync(CancellationToken.None);
        Assert.That(state.LastUpdateCheckTime, Is.EqualTo(previous));

        var current = previous.AddHours(4);
        var changedProperties = new List<string?>();
        state.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
        updater.LastCheckTime.Returns(current);
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.LastCheckTime)));

        Assert.That(new PersistentState(storage).LastUpdateCheckTime, Is.EqualTo(current));
        Assert.That(changedProperties, Is.EqualTo(new[] { nameof(PersistentState.LastUpdateCheckTime) }));

        updater.LastCheckTime.Returns((DateTimeOffset?)null);
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            updater, new PropertyChangedEventArgs(nameof(ISoftwareUpdater.LastCheckTime)));
        Assert.That(state.LastUpdateCheckTime, Is.EqualTo(current));
    }

    [AvaloniaTest]
    public void UpdateControl_WhenPersistentTimeChanges_RefreshesTheDisplayedDate()
    {
        EnsureShadUIResources();
        using var services = new ServiceCollection().BuildServiceProvider();
        var state = new PersistentState(new InMemoryKeyValueStorage());
        var control = new SoftwareUpdateControl(state, Substitute.For<ISoftwareUpdater>(), services);
        control.Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Everywhere.Core/Views/Configuration/SoftwareUpdateControl.axaml")));
        var window = new Window { Content = control };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var checkUpdate = window.GetVisualDescendants().OfType<CheckUpdate>().Single();
            Assert.That(checkUpdate.IsNeverChecked, Is.True);

            var current = DateTimeOffset.UtcNow;
            state.LastUpdateCheckTime = current;
            Dispatcher.UIThread.RunJobs();
            Assert.That(checkUpdate.LastUpdateCheckDate, Is.EqualTo(current.ToLocalTime()));
            Assert.That(checkUpdate.IsNeverChecked, Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ManualOperation_WhenStagesChange_DoesNotCreateOrReplayToasts()
    {
        EnsureShadUIResources();
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        var updater = Substitute.For<ISoftwareUpdater>();
        var window = new Window { Content = new ToastHost() };
        window.Show();
        try
        {
            using var initializer = new SoftwareUpdaterInitializer(
                updater, settings, new PersistentState(new InMemoryKeyValueStorage()),
                Substitute.For<INotificationPublisher<SoftwareUpdater>>(), services);
            await ((IAsyncInitializer)initializer).InitializeAsync(CancellationToken.None);
#if DEBUG
            await updater.DidNotReceive().RecoverCachedPackageAsync(Arg.Any<CancellationToken>());
#else
            await updater.Received(1).RecoverCachedPackageAsync(Arg.Any<CancellationToken>());
#endif
            foreach (var stage in Enum.GetValues<SoftwareUpdateOperationStage>())
            {
                SetOperation(updater, new SoftwareUpdateOperation(
                    stage, null, 0.25d));
                Assert.That(window.GetVisualDescendants().OfType<Toast>(), Is.Empty);
            }

            window.Content = null;
            window.Content = new ToastHost();
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.GetVisualDescendants().OfType<Toast>(), Is.Empty);
            updater.DidNotReceive().CancelCurrentOperation();
        }
        finally
        {
            window.Close();
        }
    }

    private static void EnsureShadUIResources()
    {
        if (Application.Current is not { } application) return;

        var resources = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://ShadUI/Controls/Resources.axaml"));
        application.Resources.MergedDictionaries.Add(resources);
    }

    private static void SetOperation(ISoftwareUpdater updater, SoftwareUpdateOperation operation)
    {
        updater.CurrentOperation.Returns(_ => operation);
        updater.PropertyChanged += Raise.Event<PropertyChangedEventHandler>(
            updater,
            new PropertyChangedEventArgs(nameof(ISoftwareUpdater.CurrentOperation)));
        Dispatcher.UIThread.RunJobs();
    }

}