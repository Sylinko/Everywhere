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
    public async Task AutomaticChecking_WhenStartedAndReenabled_UsesFourHourInterval()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        settings.Common.IsAutomaticUpdateCheckEnabled = true;
        var updater = Substitute.For<ISoftwareUpdater>();
        var initializer = new SoftwareUpdaterInitializer(
            updater, settings, new PersistentState(new InMemoryKeyValueStorage()),
            Substitute.For<INotificationPublisher<SoftwareUpdater>>(), services);

        await initializer.InitializeAsync(CancellationToken.None);
        updater.Received(1).RunAutomaticCheckInBackground(TimeSpan.FromHours(4), Arg.Any<CancellationToken>());

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
        var initializer = new SoftwareUpdaterInitializer(
            updater, new Settings(services), state,
            Substitute.For<INotificationPublisher<SoftwareUpdater>>(), services);
        await initializer.InitializeAsync(CancellationToken.None);
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
            var initializer = new SoftwareUpdaterInitializer(
                updater, settings, new PersistentState(new InMemoryKeyValueStorage()),
                Substitute.For<INotificationPublisher<SoftwareUpdater>>(), services);
            await initializer.InitializeAsync(CancellationToken.None);
#if DEBUG
            await updater.DidNotReceive().RecoverCachedPackageAsync(Arg.Any<CancellationToken>());
#else
            await updater.Received(1).RecoverCachedPackageAsync(Arg.Any<CancellationToken>());
#endif
            foreach (var stage in Enum.GetValues<SoftwareUpdateOperationStage>())
            {
                SetOperation(updater, new SoftwareUpdateOperation(
                    1, SoftwareUpdateOperationIntent.Manual, stage, null, 0.25d));
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
