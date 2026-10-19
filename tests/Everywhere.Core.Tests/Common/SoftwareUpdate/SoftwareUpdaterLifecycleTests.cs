using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ShadUI;

namespace Everywhere.Core.Tests.Common.SoftwareUpdate;

[TestFixture]
[NonParallelizable]
public sealed class SoftwareUpdaterLifecycleTests
{
    [AvaloniaTest]
    public async Task ManualFailure_WhenDownloadFails_ReturnsTheLocalizedErrorToTheCaller()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new IOException("Download failed")));
        using var updater = CreateUpdater(services, Substitute.For<IPlatformUpdateHandler>(), downloader);
        Exception? failure = null;
        try
        {
            await updater.PerformUpdateAsync(CreateTarget());
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.That(failure, Is.TypeOf<HandledException>());
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Failed));
        Assert.That(updater.CurrentOperation?.Error, Is.SameAs(failure));
    }

    [AvaloniaTest]
    public async Task ManualCancellation_WhenWriterIsStillClosing_RetainsOwnerUntilCleanupCompletes()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var downloader = Substitute.For<IFileDownloadService>();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                using var registration = token.Register(() => cancellationObserved.TrySetResult());
                started.TrySetResult();
                await releaseWriter.Task;
                token.ThrowIfCancellationRequested();
                return call.Arg<FileDownloadRequest>().DestinationPath;
            });
        using var updater = CreateUpdater(services, platform, downloader);
        var target = CreateTarget();
        var operation = updater.PerformUpdateAsync(target);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            updater.CancelCurrentOperation();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await Task.WhenAny(operation, Task.Delay(100)), Is.Not.SameAs(operation));
            Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Cancelling));
            Assert.That(updater.PerformUpdateAsync(target), Is.SameAs(operation));
            Assert.Throws<InvalidOperationException>(() => updater.PerformUpdateAsync(CreateTarget("99.0.1")));
            Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));
        }
        finally
        {
            updater.CancelCurrentOperation();
            releaseWriter.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Canceled));
        await platform.DidNotReceive().ExecuteUpdateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new IOException("New writer reached")));
        var nextTarget = CreateTarget("99.0.1");
        await Assert.ThatAsync(
            () => updater.PerformUpdateAsync(nextTarget),
            Throws.TypeOf<HandledException>().With.InnerException.TypeOf<IOException>());
        Assert.That(updater.CurrentOperation?.Target, Is.EqualTo(nextTarget));
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task Recovery_WhenPreviouslyConfirmed_RetainsInstallablePackageButRequiresFreshDiscovery()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        var downloader = Substitute.For<IFileDownloadService>();
        var target = CreateTarget();
        await WriteCachedPackageAsync(target);
        using var updater = CreateUpdater(services, platform, downloader);

        await updater.RecoverCachedPackageAsync();
        Assert.That(updater.ReadyUpdate?.Tag, Is.EqualTo(target.Tag));
        Assert.That(updater.IsReadyUpdateConfirmedLatest, Is.False);
        Assert.That(updater.LatestUpdate, Is.Null);
        Assert.That(updater.IsUpdateActionVisible, Is.True);

        // A cached fallback remains explicitly installable without network discovery.
        await updater.PerformUpdateAsync();
        await platform.Received(1).ExecuteUpdateAsync(
            Path.Combine(RuntimeConstants.WritableFolderPath, "updates", target.Asset.Name), CancellationToken.None);
        await downloader.DidNotReceive().DownloadAsync(
            Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task Recovery_WhenPackageDigestIsInvalid_DoesNotExposeReadyAction()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        await WriteCachedPackageAsync(target);
        await File.WriteAllBytesAsync(Path.Combine(RuntimeConstants.WritableFolderPath, "updates", target.Asset.Name), [9, 9, 9]);
        using var updater = CreateUpdater(services, Substitute.For<IPlatformUpdateHandler>(), Substitute.For<IFileDownloadService>());
        await updater.RecoverCachedPackageAsync();
        Assert.That(updater.ReadyUpdate, Is.Null);
        Assert.That(updater.IsUpdateActionVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task ChangeLog_WhenOpenedOfflineWithoutStartupRecovery_ExposesCachedFallback()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        await WriteCachedPackageAsync(target);
        using var updater = CreateUpdater(services, Substitute.For<IPlatformUpdateHandler>(), Substitute.For<IFileDownloadService>());
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new OfflineHandler()));
        using var viewModel = new ChangeLogViewModel(updater, factory, NullLogger<ChangeLogViewModel>.Instance);
        var window = new Window { Content = new ToastHost() };
        window.Show();
        try
        {
            Assert.That(updater.ReadyUpdate, Is.Null);
            await viewModel.ViewLoaded(CancellationToken.None);

            Assert.That(viewModel.UpdateAction, Is.EqualTo(UpdatePageAction.InstallFallback));
            Assert.That(viewModel.UpdateTarget?.Tag, Is.EqualTo(target.Tag));
            Assert.That(updater.IsReadyUpdateConfirmedLatest, Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ManualOperation_WhenChannelChanges_FinishesSnapshotAndUsesNewChannelNextTime()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        settings.Common.UpdateChannel = UpdateChannel.Stable;
        var stable = CreateTarget();
        var canary = CreateTarget("99.0.1-canary.1");
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(canary.Asset.Name, canary.Asset.Digest, canary.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(canary)));
        var downloader = Substitute.For<IFileDownloadService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(call.Arg<CancellationToken>());
                return call.Arg<FileDownloadRequest>().DestinationPath;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory, settings);
        var first = updater.PerformUpdateAsync(stable);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            settings.Common.UpdateChannel = UpdateChannel.Canary;
            Assert.That(updater.CurrentOperation?.Target, Is.EqualTo(stable));
            Assert.That(factory.ReceivedCalls(), Is.Empty);
        }
        finally { finish.TrySetResult(); }
        await first;
        await platform.Received(1).ExecuteUpdateAsync(Arg.Is<string>(path => path.EndsWith(stable.Asset.Name)), CancellationToken.None);

        await updater.PerformUpdateAsync();
        Assert.That(updater.CurrentOperation?.Target?.Tag, Is.EqualTo(canary.Tag));
        await platform.Received(1).ExecuteUpdateAsync(Arg.Is<string>(path => path.EndsWith(canary.Asset.Name)), CancellationToken.None);
    }

    [AvaloniaTest]
    public async Task ManualOperation_WhenCallerStopsWaiting_ContinuesAndCanBeJoined()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(call.Arg<CancellationToken>());
                return call.Arg<FileDownloadRequest>().DestinationPath;
            });
        var platform = Substitute.For<IPlatformUpdateHandler>();
        using var updater = CreateUpdater(services, platform, downloader);
        var target = CreateTarget();
        var wait = updater.PerformUpdateAsync(target, caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThatAsync(async () => await wait, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Downloading));
        var operation = updater.PerformUpdateAsync(target);
        finish.TrySetResult();
        await operation;
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));
        await platform.Received(1).ExecuteUpdateAsync(Arg.Any<string>(), CancellationToken.None);
    }

    [AvaloniaTest]
    public async Task ExactRelease_WhenChannelChangesDuringRequest_ReturnsOriginalSnapshot()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        settings.Common.UpdateChannel = UpdateChannel.Stable;
        var target = CreateTarget();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target, async token =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
        })));
        using var updater = CreateUpdater(services, platform, Substitute.For<IFileDownloadService>(), factory, settings);
        var request = updater.ResolveUpdateAsync(UpdateChannel.Stable, target.Tag);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.Common.UpdateChannel = UpdateChannel.Canary;
        finish.TrySetResult();
        Assert.That((await request).Tag, Is.EqualTo(target.Tag));
        Assert.That(factory.ReceivedCalls().Count(), Is.EqualTo(1));
    }

#if DEBUG
    [AvaloniaTest]
    public async Task ChannelChange_InDebug_DoesNotStartWork()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        settings.Common.UpdateChannel = UpdateChannel.Stable;
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        using var updater = new SoftwareUpdater(settings, platform, factory, Substitute.For<IFileDownloadService>(),
            Substitute.For<INativeHelper>(), NullLogger<SoftwareUpdater>.Instance);

        updater.RunAutomaticCheckInBackground(TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        Assert.That(factory.ReceivedCalls(), Is.Empty);

        // A user-requested check still works in Debug.
        await updater.CheckForUpdatesAsync(throwOnError: true);
        Assert.That(updater.LatestUpdate?.Tag, Is.EqualTo(target.Tag));
        factory.ClearReceivedCalls();

        settings.Common.UpdateChannel = UpdateChannel.Canary;


        Assert.That(updater.LatestUpdate, Is.Null);
        Assert.That(updater.IsUpdateActionVisible, Is.False);
        Assert.That(factory.ReceivedCalls(), Is.Empty);
    }
#else
    [AvaloniaTest]
    public async Task AutomaticDownload_WhenConfirmationFails_RetainsValidatedOfflineInstall()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var requests = 0;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target, _ =>
            Interlocked.Increment(ref requests) == 1 ? Task.CompletedTask : Task.FromException(new HttpRequestException("Offline")))));
        var downloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var path = call.Arg<FileDownloadRequest>().DestinationPath;
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                downloaded.TrySetResult();
                return path;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4));
        await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await GetOperationTask(updater).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.ReadyUpdate?.Tag, Is.EqualTo(target.Tag));
        Assert.That(updater.IsReadyUpdateConfirmedLatest, Is.False);
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Completed));
        await updater.PerformUpdateAsync();
        Assert.That(requests, Is.EqualTo(2));
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));
        await platform.Received(1).ExecuteUpdateAsync(Arg.Any<string>(), CancellationToken.None);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task AutomaticPolicy_WhenReleaseIsYoungOrConnectionIsMetered_DoesNotDownload(bool isMetered)
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        var target = CreateTarget() with { PublishedAt = DateTimeOffset.UtcNow.AddMinutes(isMetered ? -120 : -10) };
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        var downloader = Substitute.For<IFileDownloadService>();
        var native = Substitute.For<INativeHelper>();
        native.IsLowDataModeActive.Returns(isMetered);
        var settings = new Settings(services);
        settings.Common.UpdateChannel = UpdateChannel.Stable;
        using var updater = new SoftwareUpdater(settings, platform, factory, downloader, native, NullLogger<SoftwareUpdater>.Instance);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromMilliseconds(50), stop.Token);
        await updater.CheckForUpdatesAsync(true);
        await Task.Delay(160);
        stop.Cancel();
        Assert.That(updater.LatestUpdate?.Tag, Is.EqualTo(target.Tag));
        Assert.That(downloader.ReceivedCalls(), Is.Empty);
    }

    [AvaloniaTest]
    public async Task AutomaticDownload_WhenChannelChanges_ConfirmsOriginalChannelWithoutInterrupting()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        settings.Common.UpdateChannel = UpdateChannel.Stable;
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(_ => new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(call.Arg<CancellationToken>());
                var path = call.Arg<FileDownloadRequest>().DestinationPath;
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                return path;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory, settings);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = GetOperationTask(updater);
        settings.Common.UpdateChannel = UpdateChannel.Canary;
        Assert.That(updater.DisplayUpdate?.Tag, Is.EqualTo(target.Tag));
        finish.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Completed));
        Assert.That(factory.ReceivedCalls().Count(), Is.EqualTo(2));
        var manifest = JsonSerializer.Deserialize<SoftwareUpdatePackageManifest>(
            await File.ReadAllTextAsync(Path.Combine(RuntimeConstants.WritableFolderPath, "updates", "update-package.json")));
        Assert.That(manifest?.Tag, Is.EqualTo(target.Tag));
        Assert.That(manifest?.IsConfirmedLatest, Is.True);

        target = CreateTarget("99.0.1-canary.1");
        await updater.CheckForUpdatesAsync(true);
        Assert.That(updater.LatestUpdate?.Tag, Is.EqualTo(target.Tag));
        // Stop any next-channel automatic work before the isolated directory is removed.
        updater.Dispose();
        await GetOperationTask(updater).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    public async Task AutomaticValidation_WhenCancelled_CancelsConfirmationRequestAndRetainsNoFailure()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var confirming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target, async token =>
        {
            if (Interlocked.Increment(ref requests) != 2) return;
            confirming.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        })));
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<FileDownloadRequest>().DestinationPath));
        using var updater = CreateUpdater(services, platform, downloader, factory);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4));
        await confirming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.CurrentOperation?.CanCancel, Is.True);
        var operation = GetOperationTask(updater);
        updater.CancelCurrentOperation();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Canceled));
        Assert.That(updater.CurrentOperation?.Error, Is.Null);
        await updater.CheckForUpdatesAsync(true);
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task AutomaticDownload_WhenNewReleaseIsDiscovered_FinishesWriterThenPreparesLatest()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var firstTarget = CreateTarget();
        var latest = firstTarget;
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(_ => new UpdateAssetMetadata(latest.Asset.Name, latest.Asset.Digest, latest.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(latest)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadedLatest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<FileDownloadRequest>();
                if (request.DestinationPath.EndsWith(firstTarget.Asset.Name))
                {
                    started.TrySetResult();
                    await finish.Task.WaitAsync(call.Arg<CancellationToken>());
                }
                else downloadedLatest.TrySetResult();
                await File.WriteAllBytesAsync(request.DestinationPath, [1, 2, 3]);
                return request.DestinationPath;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = GetOperationTask(updater);
        try
        {
            latest = CreateTarget("99.0.1");
            await updater.CheckForUpdatesAsync(true);
            Assert.That(updater.CurrentOperation?.Target?.Tag, Is.EqualTo(firstTarget.Tag));
            Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Downloading));
            Assert.Throws<InvalidOperationException>(() => updater.PerformUpdateAsync(firstTarget));
            Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));
        }
        finally { finish.TrySetResult(); }
        await first;
        await downloadedLatest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await GetOperationTask(updater).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.ReadyUpdate?.Tag, Is.EqualTo(latest.Tag));
        Assert.That(updater.IsReadyUpdateConfirmedLatest, Is.True);
        Assert.That(File.Exists(Path.Combine(RuntimeConstants.WritableFolderPath, "updates", firstTarget.Asset.Name)), Is.False);
        await platform.DidNotReceive().ExecuteUpdateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task AutomaticCancellation_WhenChecksContinue_DoesNotRestartUntilExplicitRetry()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return call.Arg<FileDownloadRequest>().DestinationPath;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromMilliseconds(50));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = GetOperationTask(updater);
        updater.CancelCurrentOperation();
        await operation;
        await updater.CheckForUpdatesAsync(true);
        await Task.Delay(160);
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Canceled));
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));

        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<FileDownloadRequest>().DestinationPath));
        await updater.PerformUpdateAsync(target);
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(2));
        await platform.Received(1).ExecuteUpdateAsync(Arg.Any<string>(), CancellationToken.None);
    }

    [AvaloniaTest]
    public async Task AutomaticStartup_WhenReleaseIsStable_DownloadsAndConfirmsWithoutInstalling()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        var downloader = Substitute.For<IFileDownloadService>();
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<FileDownloadRequest>();
                Assert.That(request.BytesPerSecondLimit, Is.EqualTo(1024 * 1024));
                await File.WriteAllBytesAsync(request.DestinationPath, [1, 2, 3]);
                return request.DestinationPath;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (updater.CurrentOperation?.Stage != SoftwareUpdateOperationStage.Completed && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Completed));
        Assert.That(updater.ReadyUpdate?.Tag, Is.EqualTo(target.Tag));
        Assert.That(updater.IsReadyUpdateConfirmedLatest, Is.True);
        await platform.DidNotReceive().ExecuteUpdateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task AutomaticDownload_AfterThreeFailures_BlocksSameReleaseButAllowsManualAndNewRelease()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(_ => new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        var downloader = Substitute.For<IFileDownloadService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.TrySetResult();
                return result.Task;
            });
        using var updater = CreateUpdater(services, platform, downloader, factory);
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (Gate(updater)) AutomaticRetryAt(updater) = DateTimeOffset.MinValue;
                await updater.CheckForUpdatesAsync(throwOnError: true);
            }

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var workflow = GetOperationTask(updater);
            result.SetException(new IOException("Automatic download failed"));
            await workflow.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Failed));
            Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(attempt));
            await updater.CheckForUpdatesAsync(true);
            Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(attempt), "Discovery must not bypass retry backoff.");
        }

        // Fresh discovery, including the periodic loop's entry point, cannot bypass the budget.
        for (var check = 0; check < 2; check++)
            await updater.CheckForUpdatesAsync(throwOnError: true);
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(3));

        await Assert.ThatAsync(() => updater.PerformUpdateAsync(target), Throws.TypeOf<HandledException>());
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(4));

        target = CreateTarget("99.0.1");
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await updater.CheckForUpdatesAsync(throwOnError: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newReleaseWorkflow = GetOperationTask(updater);
        result.SetException(new IOException("New release download failed"));
        await newReleaseWorkflow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.CurrentOperation?.Target?.Tag, Is.EqualTo(target.Tag));
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(5));
    }

    [AvaloniaTest]
    public async Task AutomaticStop_WhenDownloadIsActive_CancelsUnderOwnershipLockAndAllowsCompletion()
    {
        using var folder = new IsolatedUpdateFolder();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var target = CreateTarget();
        var platform = Substitute.For<IPlatformUpdateHandler>();
        platform.SelectAsset(Arg.Any<IEnumerable<UpdateAssetMetadata>>(), Arg.Any<string>())
            .Returns(new UpdateAssetMetadata(target.Asset.Name, target.Asset.Digest, target.Asset.Size));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ReleaseHandler(target)));
        var downloader = Substitute.For<IFileDownloadService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var updater = CreateUpdater(services, platform, downloader, factory);
        downloader.DownloadAsync(Arg.Any<FileDownloadRequest>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                using var registration = token.Register(() => cancellationObserved.TrySetResult(Gate(updater).IsHeldByCurrentThread));
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return call.Arg<FileDownloadRequest>().DestinationPath;
            });
        updater.RunAutomaticCheckInBackground(TimeSpan.FromHours(4), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workflow = GetOperationTask(updater);

        cancellation.Cancel();

        Assert.That(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        await workflow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(updater.CurrentOperation?.Stage, Is.EqualTo(SoftwareUpdateOperationStage.Canceled));
        Assert.That(downloader.ReceivedCalls().Count(), Is.EqualTo(1));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_automaticRetryAt")]
    private static extern ref DateTimeOffset AutomaticRetryAt(SoftwareUpdater updater);
#endif

    private static Task GetOperationTask(SoftwareUpdater updater)
    {
        lock (Gate(updater))
        {
            return OperationTask(updater) ?? throw new InvalidOperationException("No update operation has started.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref Lock Gate(SoftwareUpdater updater);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_operationTask")]
    private static extern ref Task? OperationTask(SoftwareUpdater updater);

    private static SoftwareUpdater CreateUpdater(
        IServiceProvider services, IPlatformUpdateHandler platform, IFileDownloadService downloader, IHttpClientFactory? factory = null, Settings? settings = null)
    {
        settings ??= new Settings(services);
        if (settings.Common.UpdateChannel == UpdateChannel.Unknown) settings.Common.UpdateChannel = UpdateChannel.Stable;
        return new SoftwareUpdater(settings, platform, factory ?? Substitute.For<IHttpClientFactory>(), downloader,
            Substitute.For<INativeHelper>(), NullLogger<SoftwareUpdater>.Instance);
    }

    private static SoftwareUpdateMetadata CreateTarget(string versionString = "99.0.0")
    {
        if (!SemanticVersion.TryParse(versionString, out var version)) throw new InvalidOperationException("Invalid test version.");
        return new SoftwareUpdateMetadata(version.Channel, $"v{versionString}", version, DateTimeOffset.UtcNow.AddDays(-1),
            new SoftwareUpdateMetadata.UpdateAsset($"mock-{versionString}.exe", Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })),
                3, "https://example.invalid/proxy", "https://example.invalid/direct"));
    }

    private static async Task WriteCachedPackageAsync(SoftwareUpdateMetadata target)
    {
        var updatesPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
        await File.WriteAllBytesAsync(Path.Combine(updatesPath, target.Asset.Name), [1, 2, 3]);
        var manifest = new SoftwareUpdatePackageManifest(target.Channel.ToString(), target.Tag, target.Version.ToString(),
            target.PublishedAt, target.Asset.Name, target.Asset.Digest, target.Asset.Size, SoftwareUpdater.PackageProvenance.Automatic, true);
        await File.WriteAllTextAsync(Path.Combine(updatesPath, "update-package.json"), JsonSerializer.Serialize(manifest));
    }

    // Keep the real runtime entry points while isolating all package and manifest I/O from user data.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "<WritableFolderPath>k__BackingField")]
    private static extern ref string WritableFolderPath(
        [UnsafeAccessorType("Everywhere.Common.RuntimeConstants, Everywhere.Abstractions")] object? unused);

    private sealed class IsolatedUpdateFolder : IDisposable
    {
        private readonly string _original = RuntimeConstants.WritableFolderPath;
        private readonly string _path = Path.Combine(Path.GetTempPath(), "Everywhere-update-tests", Guid.NewGuid().ToString("N"));

        public IsolatedUpdateFolder()
        {
            Directory.CreateDirectory(_path);
            WritableFolderPath(null) = _path;
        }

        public void Dispose()
        {
            WritableFolderPath(null) = _original;
            Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class ReleaseHandler(SoftwareUpdateMetadata target, Func<CancellationToken, Task>? beforeResponse = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (beforeResponse is not null) await beforeResponse(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = target.Tag,
                    published_at = target.PublishedAt,
                    assets = new[] { new { name = target.Asset.Name, size = target.Asset.Size, digest = target.Asset.Digest } }
                }))
            };
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}