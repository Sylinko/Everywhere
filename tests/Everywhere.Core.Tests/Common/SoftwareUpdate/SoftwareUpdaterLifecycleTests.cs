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

#if DEBUG
    [AvaloniaTest]
    public async Task ChannelChange_InDebug_InvalidatesDiscoveryWithoutStartingAnotherCheck()
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

        // A user-requested check still works in Debug.
        await updater.CheckForUpdatesAsync(throwOnError: true);
        Assert.That(updater.LatestUpdate?.Tag, Is.EqualTo(target.Tag));
        factory.ClearReceivedCalls();

        settings.Common.UpdateChannel = UpdateChannel.Canary;
        await updater.HandleUpdateChannelChangedAsync();

        Assert.That(updater.LatestUpdate, Is.Null);
        Assert.That(updater.IsUpdateActionVisible, Is.False);
        Assert.That(factory.ReceivedCalls(), Is.Empty);
    }
#else
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
#endif

    private static SoftwareUpdater CreateUpdater(
        IServiceProvider services, IPlatformUpdateHandler platform, IFileDownloadService downloader, IHttpClientFactory? factory = null)
    {
        var settings = new Settings(services);
        settings.Common.UpdateChannel = UpdateChannel.Stable;
        return new SoftwareUpdater(settings, platform, factory ?? Substitute.For<IHttpClientFactory>(), downloader,
            Substitute.For<INativeHelper>(), NullLogger<SoftwareUpdater>.Instance);
    }

    private static SoftwareUpdateMetadata CreateTarget(string versionString = "99.0.0")
    {
        if (!SemanticVersion.TryParse(versionString, out var version)) throw new InvalidOperationException("Invalid test version.");
        return new SoftwareUpdateMetadata(UpdateChannel.Stable, $"v{versionString}", version, DateTimeOffset.UtcNow.AddDays(-1),
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

    private sealed class ReleaseHandler(SoftwareUpdateMetadata target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = target.Tag,
                    published_at = target.PublishedAt,
                    assets = new[] { new { name = target.Asset.Name, size = target.Asset.Size, digest = target.Asset.Digest } }
                }))
            });
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
