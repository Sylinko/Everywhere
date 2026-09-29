using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using NSubstitute;
using SkiaSharp;

namespace Everywhere.Core.Tests.ProcessIsolation;

[TestFixture]
public sealed class AutomationHostSessionTests
{
    [Test]
    public async Task RemoteAnchor_WhenQueryReturnsStructuredFailure_PreservesMessagesAcrossRpc()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend
        {
            ElementQueryFailure = new VisualElementQueryFailure(
                VisualElementQueryFailureKind.LimitReached,
                new DynamicLocaleKey("test.visual.limit"),
                new InvalidOperationException("host-only detail"),
                "The visual relation reached its test limit."),
        };
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateContextAsync();
        using var anchor = await context.AcquireAnchorAsync(VisualElementLocator.Default) ??
                           throw new InvalidOperationException("The test Backend did not return its default element.");

        Assert.Multiple(() =>
        {
            Assert.That(anchor.FailureKind, Is.EqualTo(VisualElementQueryFailureKind.LimitReached));
            Assert.That(anchor.FailureMessage, Is.TypeOf<DynamicLocaleKey>());
            Assert.That(((DynamicLocaleKey)anchor.FailureMessage!).Key, Is.EqualTo("test.visual.limit"));
            Assert.That(anchor.FailureAgentMessage, Is.EqualTo("The visual relation reached its test limit."));
        });

        var exception = Assert.ThrowsAsync<HandledException>(async () =>
            await context.GetElementSnapshotAsync(anchor, VisualElementFields.Bounds));
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("The visual relation reached its test limit."));
            Assert.That(exception.InnerException, Is.TypeOf<VisualElementProviderException>());
            Assert.That(exception.FriendlyMessageKey, Is.TypeOf<DynamicLocaleKey>());
            Assert.That(((DynamicLocaleKey)exception.FriendlyMessageKey).Key, Is.EqualTo("test.visual.limit"));
        });
    }

    [Test]
    public async Task RemoteContext_WhenQueriedAndReleased_PreservesAutomationIdentityAndLifetime()
    {
        var pipeName = $"evat.{Guid.NewGuid():N}"[..13];
        await using var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var serverConnection = new RpcConnection(serverStream, isServer: true, options);
        await using var clientConnection = new RpcConnection(clientStream, isServer: false, options);
        var backend = new TestBackend { ElementType = VisualElementType.TopLevel };
        await using var session = new AutomationHostSession(backend);
        session.Bind(serverConnection);
        serverConnection.Start();
        clientConnection.Start();
        var client = new AutomationHostClient(clientConnection);
        var context = await client.CreateContextAsync();

        await context.EnsureTurnAsync();
        var releasedAnchor = await context.AcquireAnchorAsync(VisualElementLocator.Default);
        if (releasedAnchor is null) throw new InvalidOperationException("The test Backend did not return its default element.");
        releasedAnchor.Dispose();
        await WaitForAsync(() => backend.ElementReleaseCount == 1);

        var anchor = await context.AcquireAnchorAsync(
            VisualElementLocator.Default,
            query: new VisualElementQueryRequest(VisualElementFields.All, 32));
        if (anchor is null) throw new InvalidOperationException("The test Backend did not return its default element.");
        using (anchor)
        {
            var refreshedSnapshot = await context.GetElementSnapshotAsync(anchor, VisualElementFields.Bounds);
            Assert.Multiple(() =>
            {
                Assert.That(refreshedSnapshot.Bounds, Is.EqualTo(new PixelRect(0, 0, 100, 20)));
                Assert.That(backend.LastRequestedFields, Is.EqualTo(VisualElementFields.Bounds));
                Assert.That(backend.LastMaxTextCharacters, Is.Zero);
            });
            using var anchorCapture = await context.CaptureAnchorAsync(anchor);
            var initial = await context.BuildAnchorsAsync(
                [anchor],
                new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin });
            byte[]? scanCapture = null;
            PixelFormat? scanCaptureFormat = null;
            SKImage? scanImage = null;
            var streamed = await context.BuildAnchorsWithCapturesAsync(
                [anchor],
                new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin, Limit = 16 },
                targetTokenBudget: 4096,
                captureReceiver: capture =>
                {
                    using (capture)
                    {
                        scanCaptureFormat = capture.Format;
                        scanCapture = ReadCapture(capture);
                        scanImage = capture.ToSKImage();
                    }
                });
            Assert.Multiple(() =>
            {
                Assert.That(anchor.Snapshot.Name, Is.EqualTo("Root"));
                Assert.That(anchor.AvailableFields, Is.EqualTo(VisualElementFields.All));
                Assert.That(ReadCapture(anchorCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
                Assert.That(initial.Content, Does.Contain("id=1"));
                Assert.That(initial.Content, Does.Contain("Root"));
                Assert.That(initial.Content, Does.Contain("hello world"));
                Assert.That(initial.Content, Does.Not.Contain("box="));
                Assert.That(initial.RepresentedTargetCount, Is.EqualTo(1));
                Assert.That(streamed.Content, Is.EqualTo(initial.Content));
                Assert.That(streamed.RepresentedTargetCount, Is.EqualTo(initial.RepresentedTargetCount));
                Assert.That(scanCaptureFormat, Is.EqualTo(PixelFormats.Gray8));
                Assert.That(scanCapture, Is.EqualTo(new byte[] { 4, 8 }));
                Assert.That(scanImage, Is.Not.Null);
            });
            scanImage?.Dispose();
        }

        await context.AdvanceTurnAsync();
        var query = await context.QueryTargetAsync(
            1,
            new VisualQueryRequest
            {
                Directions = VisualContextTraverseDirections.Origin,
                IncludesBoundingBox = true,
            });
        var text = await context.ReadTextAsync(1, limit: 5);
        using var targetCapture = await context.CaptureTargetAsync(1);
        await context.ExecuteActionsAsync(
        [
            new(AutomationActionKind.Invoke, targetId: 1),
            new(AutomationActionKind.SetText, targetId: 1, text: "updated"),
            new(AutomationActionKind.SendKey, targetId: 1, key: Key.Enter, keyModifiers: KeyModifiers.Control),
            new(AutomationActionKind.Wait, delayMilliseconds: 0),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(query.Content, Does.Contain("id=1"));
            Assert.That(query.Content, Does.Contain("box=0,0,100,20"));
            Assert.That(text, Does.Contain("hello"));
            Assert.That(ReadCapture(targetCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(backend.InvokeCount, Is.EqualTo(1));
            Assert.That(backend.LastSetText, Is.EqualTo("updated"));
            Assert.That(backend.LastKeyGesture, Is.EqualTo(new KeyGesture(Key.Enter, KeyModifiers.Control)));
        });

        context.Dispose();
        await WaitForAsync(() => backend.ElementReleaseCount == 2);
        await session.DisposeAsync();

        Assert.That(backend.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RemoteContext_WhenScanCaptureHasPaddedRowsAndMultipleChunks_StreamsAlphaOnly()
    {
        var width = 4096;
        var height = 33;
        var stride = width * 4 + 16;
        var source = new byte[stride * height];
        var expected = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            Array.Fill(source, (byte)0xee, y * stride + width * 4, stride - width * 4);
            for (var x = 0; x < width; x++)
            {
                var alpha = (byte)((y * 17 + x) % 251);
                source[y * stride + x * 4 + 3] = alpha;
                expected[y * width + x] = alpha;
            }
        }

        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend
        {
            ElementType = VisualElementType.TopLevel,
            CaptureFactory = () => new TestCapture(source, new PixelSize(width, height), stride, AlphaFormat.Premul),
        };
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateContextAsync();
        await context.EnsureTurnAsync();
        using var anchor = await context.AcquireAnchorAsync(VisualElementLocator.Default) ??
                           throw new InvalidOperationException("The test Backend did not return its default element.");
        var actual = Array.Empty<byte>();
        await context.BuildAnchorsWithCapturesAsync(
            [anchor],
            new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin, Limit = 16 },
            targetTokenBudget: 4096,
            captureReceiver: capture =>
            {
                using (capture) actual = ReadCapture(capture);
            });

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task TextSelectionMonitoring_WhenHostPushesSource_TransfersNegativeAnchorOwnership()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend { ElementType = VisualElementType.Label };
        var monitorFactory = new TestTextSelectionMonitorFactory();
        await using var session = new AutomationHostSession(backend, Substitute.For<IVisualPickerResolver>(), monitorFactory);
        session.Bind(pair.Server);
        session.OnAuthenticated(
            new RpcHandshake
            {
                AssemblyInformationalVersion = "test",
                Role = ProcessRoleNames.ToWireName(ProcessRole.Main),
                ProcessId = Environment.ProcessId,
                DesktopSessionId = "test",
            });
        var notificationReceived = new TaskCompletionSource<TextSelectionObservedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        AutomationHostNotificationRpcBinding.Bind(pair.Client, new TestAutomationNotificationSink(notificationReceived));
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateContextAsync();
        using var destinationContext = await client.CreateContextAsync();
        var monitorId = pair.Client.AllocateResourceId();
        var start = await context.StartTextSelectionMonitoringAsync(
            monitorId,
            Environment.ProcessId,
            TextSelectionMonitoringConfiguration.CreateDefault(),
            CancellationToken.None);
        var monitor = start.Monitor ?? throw new AssertionException("The monitor did not start.");
        await monitorFactory.PublishAsync("selected text");
        var notification = await notificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var anchor = context.CreatePushedAnchor(notification.AnchorId, notification.Source!);
        var snapshot = await context.GetElementSnapshotAsync(anchor, VisualElementFields.Name);
        using var capture = await context.CaptureAnchorAsync(anchor);
        using var movedAnchor = await anchor.MoveAsync(destinationContext);
        using var movedCapture = await destinationContext.CaptureAnchorAsync(movedAnchor);
        Assert.Multiple(() =>
        {
            Assert.That(notification.AnchorId, Is.LessThan(0));
            Assert.That(notification.MonitorId, Is.EqualTo(monitorId));
            Assert.That(notification.Text, Is.EqualTo("selected text"));
            Assert.That(snapshot.Name, Is.EqualTo("Root"));
            Assert.That(anchor.IsClosed, Is.True);
            Assert.That(movedAnchor.Context, Is.SameAs(destinationContext));
            Assert.That(ReadCapture(capture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(ReadCapture(movedCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        });

        monitor.Dispose();
        await WaitForAsync(() => monitorFactory.DisposeCount == 1);
    }

    [Test]
    public async Task TextSelectionMonitoring_WhenNotificationEnqueueFails_ReleasesPreparedAnchor()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend { ElementType = VisualElementType.Label };
        var monitorFactory = new TestTextSelectionMonitorFactory();
        await using var session = new AutomationHostSession(backend, Substitute.For<IVisualPickerResolver>(), monitorFactory);
        session.Bind(pair.Server);
        session.OnAuthenticated(
            new RpcHandshake
            {
                AssemblyInformationalVersion = "test",
                Role = ProcessRoleNames.ToWireName(ProcessRole.Main),
                ProcessId = Environment.ProcessId,
                DesktopSessionId = "test",
            });
        var notificationReceived = new TaskCompletionSource<TextSelectionObservedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        AutomationHostNotificationRpcBinding.Bind(pair.Client, new TestAutomationNotificationSink(notificationReceived));
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateContextAsync();
        var monitorId = pair.Client.AllocateResourceId();
        var start = await context.StartTextSelectionMonitoringAsync(
            monitorId,
            Environment.ProcessId,
            TextSelectionMonitoringConfiguration.CreateDefault(),
            CancellationToken.None);
        using var monitor = start.Monitor ?? throw new AssertionException("The monitor did not start.");
        await pair.Server.DisposeAsync();

        Assert.That(async () => await monitorFactory.PublishAsync("selected text"), Throws.Exception);
        await WaitForAsync(() => backend.ElementReleaseCount == 1);
    }

    [Test]
    public async Task AutomationTextSelectionWatcher_WhenHostPushesSource_DeliversOwnedResultToMainConsumer()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend { ElementType = VisualElementType.Label };
        var monitorFactory = new TestTextSelectionMonitorFactory();
        await using var session = new AutomationHostSession(backend, Substitute.For<IVisualPickerResolver>(), monitorFactory);
        session.Bind(pair.Server);
        session.OnAuthenticated(
            new RpcHandshake
            {
                AssemblyInformationalVersion = "test",
                Role = ProcessRoleNames.ToWireName(ProcessRole.Main),
                ProcessId = Environment.ProcessId,
                DesktopSessionId = "test",
            });
        pair.Server.Start();
        pair.Client.Start();

        var connectionSource = new WatchingTestHostConnectionSource(pair.Client);
        using var visualService = new ChatVisualService(connectionSource);
        await using var watcher = new AutomationTextSelectionWatcher(
            connectionSource,
            visualService,
            new Settings(Substitute.For<IServiceProvider>()),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<AutomationTextSelectionWatcher>>());
        var observer = new TestTextSelectionObserver();
        using var subscription = watcher.Subscribe(observer);
        watcher.UpdateConfiguration(TextSelectionMonitoringConfiguration.CreateDefault());
        Assert.That(watcher.SetEnabledCommand.CanExecute(true), Is.True);
        await watcher.SetEnabledCommand.ExecuteAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(watcher.IsEnabled, Is.True);
        await WaitForAsync(() => monitorFactory.IsStarted);
        await monitorFactory.PublishAsync("selected text");
        using var data = await observer.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var anchor = data.TakeSource();

        Assert.Multiple(() =>
        {
            Assert.That(data.Text, Is.EqualTo("selected text"));
            Assert.That(data.IsTextIncomplete, Is.False);
            Assert.That(anchor, Is.Not.Null);
            Assert.That(anchor!.Snapshot.Name, Is.EqualTo("Root"));
        });

        Assert.That(watcher.SetEnabledCommand.CanExecute(false), Is.True);
        await watcher.SetEnabledCommand.ExecuteAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(watcher.IsEnabled, Is.False);
        Assert.That(monitorFactory.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateContextAsync_WhenCancelledAfterDispatch_ReleasesResourceRegisteredLater()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        await using var registry = new RpcRemoteResourceRegistry();
        var requestStarted = new TaskCompletionSource<CreateAutomationContextRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new AsyncTestResource();
        var hostRpc = Substitute.For<IAutomationHostRpc>();
        hostRpc.CreateContextAsync(Arg.Any<CreateAutomationContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateContextAsync(call.ArgAt<CreateAutomationContextRequest>(0)));
        var releaseRpc = Substitute.For<IRpcResourceReleaseRpc>();
        releaseRpc.ReleaseResourceAsync(Arg.Any<RpcResourceReleaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ReleaseResourceAsync(call.ArgAt<RpcResourceReleaseRequest>(0)));
        AutomationHostRpcBinding.Bind(pair.Server, hostRpc);
        RpcResourceReleaseRpcBinding.Bind(pair.Server, releaseRpc);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var cancellation = new CancellationTokenSource();
        var creation = client.CreateContextAsync(cancellationToken: cancellation.Token).AsTask();
        var request = await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await creation.WaitAsync(TimeSpan.FromSeconds(5)));
            await releaseReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            allowRegistration.TrySetResult();
        }

        await WaitForAsync(() => resource.DisposeCount == 1);

        Assert.Multiple(() =>
        {
            Assert.That(request.ContextResourceId, Is.GreaterThan(0));
            Assert.That(registry.Count, Is.Zero);
            Assert.That(resource.DisposeCount, Is.EqualTo(1));
        });

        async ValueTask<Guid> CreateContextAsync(CreateAutomationContextRequest createRequest)
        {
            requestStarted.TrySetResult(createRequest);
            await allowRegistration.Task.ConfigureAwait(false);
            await registry.RegisterAsync(createRequest.ContextResourceId, resource).ConfigureAwait(false);
            return Guid.CreateVersion7();
        }

        async ValueTask<RpcAck> ReleaseResourceAsync(RpcResourceReleaseRequest releaseRequest)
        {
            var result = await registry.ReleaseResourceAsync(releaseRequest).ConfigureAwait(false);
            releaseReceived.TrySetResult();
            return result;
        }
    }

    [Test]
    public async Task RemoteDebuggerContext_WhenInspectionTurnAdvances_RejectsPreviousTargetIds()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend();
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateDebuggerContextAsync();
        await context.AdvanceTurnAsync();
        using var anchor = await context.AcquireAnchorAsync(VisualElementLocator.Default) ??
                           throw new InvalidOperationException("The test Backend did not return its default element.");
        var tree = await context.InspectAnchorAsync(anchor);
        var root = tree.Roots.Single();
        var refreshed = await context.GetTargetSnapshotAsync(root.TargetId, VisualElementFields.Bounds);

        Assert.Multiple(() =>
        {
            Assert.That(root.TargetId, Is.EqualTo(1));
            Assert.That(root.Snapshot.Name, Is.EqualTo("Root"));
            Assert.That(refreshed.Bounds, Is.EqualTo(new PixelRect(0, 0, 100, 20)));
        });

        await context.AdvanceTurnAsync();
        var exception = Assert.ThrowsAsync<RpcRemoteException>(async () =>
            await context.GetTargetSnapshotAsync(root.TargetId, VisualElementFields.Bounds));
        Assert.Multiple(() =>
        {
            Assert.That(exception?.Code, Is.EqualTo("handler_error"));
            Assert.That(exception?.RemoteMessage, Does.Contain($"Visual target {root.TargetId} is no longer available"));
        });
    }

    [Test]
    public async Task RemotePicker_WhenConfirmed_TransfersExactCandidateWithoutReacquiring()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend(shouldUsePointIdentity: true);
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateContextAsync();
        using var picker = await context.BeginPickerAsync();
        var firstObservation = await picker.UpdateAsync(new PixelPoint(20, 40), ScreenSelectionMode.Element);
        var observation = await picker.UpdateAsync(new PixelPoint(120, 240), ScreenSelectionMode.Element);
        await WaitForAsync(() => backend.ElementReleaseCount == 1);
        var anchor = await picker.ConfirmAsync(
            observation,
            new VisualElementQueryRequest(VisualElementFields.All, 32));
        if (anchor is null) throw new InvalidOperationException("The test picker did not return its current element.");

        using (anchor)
        {
            Assert.Multiple(() =>
            {
                Assert.That(observation.Snapshot?.Bounds, Is.EqualTo(new PixelRect(0, 0, 100, 20)));
                Assert.That(firstObservation.Snapshot, Is.Not.Null);
                Assert.That(anchor.Snapshot.Name, Is.EqualTo("Root"));
                Assert.That(backend.AcquisitionCount, Is.EqualTo(2));
                Assert.That(backend.LastLocator, Is.EqualTo(VisualElementLocator.FromPoint(new PixelPoint(120, 240))));
                Assert.That(backend.LastResolution, Is.EqualTo(VisualElementResolution.Direct));
                Assert.That(picker.IsClosed, Is.True);
            });
        }

        await WaitForAsync(() => backend.ElementReleaseCount == 2);
    }

    [Test]
    public async Task RemotePicker_WhenProviderDeniesUpdate_ReturnsFailureAndAcceptsNextUpdate()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend(shouldUsePointIdentity: true)
        {
            AcquisitionException = new UnauthorizedAccessException("denied by test provider"),
        };
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var context = await client.CreateContextAsync();
        using var picker = await context.BeginPickerAsync();
        var deniedObservation = await picker.UpdateAsync(new PixelPoint(20, 40), ScreenSelectionMode.Element);
        var recoveredObservation = await picker.UpdateAsync(new PixelPoint(120, 240), ScreenSelectionMode.Element);

        Assert.Multiple(() =>
        {
            Assert.That(deniedObservation.Snapshot, Is.Null);
            Assert.That(deniedObservation.FailureKind, Is.EqualTo(VisualElementQueryFailureKind.PermissionDenied));
            Assert.That(recoveredObservation.Snapshot, Is.Not.Null);
            Assert.That(recoveredObservation.FailureKind, Is.Null);
            Assert.That(picker.IsClosed, Is.False);
        });
    }

    [Test]
    public async Task VisualPickerUpdateWorker_WhenConfirmationTimesOut_ReceivesTypedExceptionAndCanContinue()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend(shouldUsePointIdentity: true);
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var source = new TestHostConnectionSource(pair.Client);
        using var visualService = new ChatVisualService(source);
        var visualContext = visualService.AcquisitionContext;
        var picker = await visualContext.BeginPickerAsync();
        using var pump = new VisualPickerUpdateWorker(visualContext, picker);
        var observationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.ObservationReceived += _ => observationReceived.TrySetResult();
        pump.Update(new PixelPoint(10, 20), ScreenSelectionMode.Element);
        await observationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        backend.ElementQueryException = new TimeoutException("test provider timeout");
        var exception = Assert.ThrowsAsync<TimeoutException>(async () => await pump.ConfirmAsync());
        Assert.Multiple(() =>
        {
            Assert.That(exception?.Message, Is.EqualTo("The Automation Host visual-element operation timed out."));
            Assert.That(exception?.Message, Does.Not.Contain("test provider timeout"));
            Assert.That(picker.IsClosed, Is.False);
        });

        observationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Update(new PixelPoint(30, 40), ScreenSelectionMode.Element);
        await observationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var anchor = await pump.ConfirmAsync() ??
                           throw new InvalidOperationException("The recovered picker did not retain its candidate.");

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Snapshot.Name, Is.EqualTo("Root"));
            Assert.That(picker.IsClosed, Is.True);
        });
    }

    [Test]
    public async Task VisualPickerUpdateWorker_WhenObservationIsUnavailable_CanContinue()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend(shouldUsePointIdentity: true)
        {
            AcquisitionException = new UnauthorizedAccessException("denied by test provider"),
        };
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var source = new TestHostConnectionSource(pair.Client);
        using var visualService = new ChatVisualService(source);
        var visualContext = visualService.AcquisitionContext;
        var picker = await visualContext.BeginPickerAsync();
        using var pump = new VisualPickerUpdateWorker(visualContext, picker);
        var observationReceived = new TaskCompletionSource<VisualPickerObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.ObservationReceived += observation => observationReceived.TrySetResult(observation);

        pump.Update(new PixelPoint(10, 20), ScreenSelectionMode.Element);
        var unavailableObservation = await observationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unavailableAnchor = await pump.ConfirmAsync();
        Assert.Multiple(() =>
        {
            Assert.That(unavailableObservation.FailureKind, Is.EqualTo(VisualElementQueryFailureKind.PermissionDenied));
            Assert.That(unavailableAnchor, Is.Null);
            Assert.That(picker.IsClosed, Is.False);
        });

        observationReceived = new TaskCompletionSource<VisualPickerObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Update(new PixelPoint(30, 40), ScreenSelectionMode.Element);
        var recoveredObservation = await observationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var anchor = await pump.ConfirmAsync() ??
                           throw new InvalidOperationException("The recovered picker did not retain its candidate.");

        Assert.Multiple(() =>
        {
            Assert.That(recoveredObservation.Snapshot, Is.Not.Null);
            Assert.That(anchor.Snapshot.Name, Is.EqualTo("Root"));
            Assert.That(picker.IsClosed, Is.True);
        });
    }

    [Test]
    public async Task RpcStream_WhenMappedFailureFollowsChunk_RestoresTypedException()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        pair.Server.RegisterExceptionMapper(AutomationRpcExceptionMapper.Shared);
        pair.Client.RegisterExceptionMapper(AutomationRpcExceptionMapper.Shared);
        pair.Server.RegisterStreamHandler<int, int>(0x7FFF0002, ProduceStream);
        pair.Server.Start();
        pair.Client.Start();
        var values = new List<int>();

        var exception = Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var value in pair.Client.InvokeStreamAsync<int, int>(0x7FFF0002, 0)) values.Add(value);
        });

        Assert.Multiple(() =>
        {
            Assert.That(values, Is.EqualTo(new[] { 1 }));
            Assert.That(exception?.Message, Is.EqualTo("The Automation Host visual-element operation timed out."));
            Assert.That(exception?.Message, Does.Not.Contain("test stream timeout"));
        });

        static async IAsyncEnumerable<int> ProduceStream(int _, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 1;
            await Task.Yield();
            throw new TimeoutException("test stream timeout");
        }
    }

    [Test]
    public async Task RemoteAnchor_WhenMovedBetweenContexts_ConsumesOnlySourceOwnerAndUsesDestinationCanonicalElement()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend();
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var client = new AutomationHostClient(pair.Client);
        using var sourceContext = await client.CreateContextAsync();
        using var destinationContext = await client.CreateContextAsync();
        using var sourceAnchor = await sourceContext.AcquireAnchorAsync(VisualElementLocator.Default) ??
                                 throw new InvalidOperationException("The test Backend did not return its default element.");
        using var otherSourceAnchor = await sourceContext.AcquireAnchorAsync(VisualElementLocator.Default) ??
                                      throw new InvalidOperationException("The test Backend did not return its default element.");
        using var existingDestinationAnchor = await destinationContext.AcquireAnchorAsync(VisualElementLocator.Default) ??
                                              throw new InvalidOperationException("The test Backend did not return its default element.");

        using var movedAnchor = await sourceAnchor.MoveAsync(destinationContext);
        using var sourceCapture = await sourceContext.CaptureAnchorAsync(otherSourceAnchor);
        using var destinationCapture = await destinationContext.CaptureAnchorAsync(movedAnchor);

        Assert.Multiple(() =>
        {
            Assert.That(sourceAnchor.IsClosed, Is.True);
            Assert.That(movedAnchor.Context, Is.SameAs(destinationContext));
            Assert.That(movedAnchor.Snapshot.Name, Is.EqualTo("Root"));
            Assert.That(ReadCapture(sourceCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(ReadCapture(destinationCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(backend.ElementReleaseCount, Is.Zero);
        });

        otherSourceAnchor.Dispose();
        existingDestinationAnchor.Dispose();
        movedAnchor.Dispose();
        await WaitForAsync(() => backend.ElementReleaseCount == 2);
    }

    [Test]
    public async Task VisualPickerUpdateWorker_WhenUpdatesArriveDuringQuery_ProcessesFirstAndLatestCoordinates()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueQuery = new ManualResetEventSlim();
        var backend = new TestBackend(queryStarted, continueQuery, shouldUsePointIdentity: true);
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var source = new TestHostConnectionSource(pair.Client);
        using var visualService = new ChatVisualService(source);
        var visualContext = visualService.AcquisitionContext;
        var picker = await visualContext.BeginPickerAsync();
        using var pump = new VisualPickerUpdateWorker(visualContext, picker);
        var latestObservationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observationCount = 0;
        pump.ObservationReceived += _ =>
        {
            if (Interlocked.Increment(ref observationCount) == 2) latestObservationReceived.TrySetResult();
        };

        try
        {
            pump.Update(new PixelPoint(10, 20), ScreenSelectionMode.Element);
            await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pump.Update(new PixelPoint(30, 40), ScreenSelectionMode.Element);
            pump.Update(new PixelPoint(50, 60), ScreenSelectionMode.Element);
        }
        finally
        {
            continueQuery.Set();
        }

        await latestObservationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var anchor = await pump.ConfirmAsync() ??
                           throw new InvalidOperationException("The test picker did not retain its latest candidate.");

        Assert.Multiple(() =>
        {
            Assert.That(observationCount, Is.EqualTo(2));
            Assert.That(backend.AcquisitionCount, Is.EqualTo(2));
            Assert.That(backend.LastLocator, Is.EqualTo(VisualElementLocator.FromPoint(new PixelPoint(50, 60))));
            Assert.That(anchor.Snapshot.Name, Is.EqualTo("Root"));
        });
    }

    [Test]
    public async Task BuildDefaultAsync_WhenBackendQueryBlocks_DoesNotBlockTransportDispatch()
    {
        var pipeName = $"evat.{Guid.NewGuid():N}"[..13];
        await using var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var serverConnection = new RpcConnection(serverStream, isServer: true, options);
        await using var clientConnection = new RpcConnection(clientStream, isServer: false, options);
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueQuery = new ManualResetEventSlim();
        var backend = new TestBackend(queryStarted, continueQuery);
        await using var session = new AutomationHostSession(backend);
        session.Bind(serverConnection);
        serverConnection.RegisterRequestHandler<int, int>(0x7FFF0001, static (value, _) => ValueTask.FromResult(value + 1));
        serverConnection.Start();
        clientConnection.Start();
        var client = new AutomationHostClient(clientConnection);
        using var context = await client.CreateContextAsync();
        await context.EnsureTurnAsync();

        // This exercises real duplex RPC with a deliberately blocked substitute Backend. It does
        // not cover platform UIA/AX behavior or native timeout handling.
        var query = context.BuildDefaultAsync(
            new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin }).AsTask();
        int controlResponse;
        try
        {
            await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            controlResponse = await clientConnection.InvokeAsync<int, int>(0x7FFF0001, 41).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            continueQuery.Set();
        }

        await query.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(controlResponse, Is.EqualTo(42));
    }

    [Test]
    public async Task ChatVisualService_WhenConnectionIsReplaced_ChangesContextIdentityAndInvalidatesTargets()
    {
        await using var firstPair = await TestConnectionPair.CreateAsync();
        await using var firstSession = new AutomationHostSession(new TestBackend());
        firstSession.Bind(firstPair.Server);
        firstPair.Server.Start();
        firstPair.Client.Start();

        var source = new TestHostConnectionSource(firstPair.Client);
        using var visualService = new ChatVisualService(source);
        using var visualState = new ChatVisualState();
        await visualService.EnsureTurnAsync(visualState);
        var firstContextId = await visualService.GetCurrentContextIdAsync(visualState, CancellationToken.None);
        using var acquisitionAnchor = await visualService.AcquireAnchorAsync(VisualElementLocator.Default) ??
                                      throw new InvalidOperationException("The test Backend did not return its default element.");
        using var oldAnchor = await visualService.MoveAnchorAsync(visualState, acquisitionAnchor);
        var initial = await visualService.BuildAnchorsAsync(
            visualState,
            [oldAnchor],
            new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin });
        Assert.That(initial.Value.RepresentedTargetCount, Is.EqualTo(1));
        _ = await visualService.QueryTargetAsync(
            visualState,
            1,
            new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin });

        // Replacing this source exercises Main-side connection state without simulating a Host crash.
        await using var secondPair = await TestConnectionPair.CreateAsync();
        await using var secondSession = new AutomationHostSession(new TestBackend());
        secondSession.Bind(secondPair.Server);
        secondPair.Server.Start();
        secondPair.Client.Start();
        source.Current = secondPair.Client;

        await visualService.EnsureTurnAsync(visualState);
        var secondContextId = await visualService.GetCurrentContextIdAsync(visualState, CancellationToken.None);
        var staleQuery = Assert.ThrowsAsync<VisualContextResetException>(async () =>
            await visualService.QueryTargetAsync(
                visualState,
                1,
                new VisualQueryRequest { Directions = VisualContextTraverseDirections.Origin }));

        Assert.Multiple(() =>
        {
            Assert.That(secondContextId, Is.Not.EqualTo(firstContextId));
            Assert.That(staleQuery?.Message, Is.EqualTo(ChatVisualService.ResetNotice));
        });
    }

    [Test]
    public async Task ChatVisualService_WhenSecondMoveFails_PreservesMovedAndRemainingDraftOwnership()
    {
        await using var pair = await TestConnectionPair.CreateAsync();
        var backend = new TestBackend(shouldUsePointIdentity: true);
        await using var session = new AutomationHostSession(backend);
        session.Bind(pair.Server);
        pair.Server.Start();
        pair.Client.Start();

        var source = new TestHostConnectionSource(pair.Client);
        using var visualService = new ChatVisualService(source);
        using var visualState = new ChatVisualState();
        using var firstSource = await visualService.AcquireAnchorAsync(VisualElementLocator.FromPoint(new PixelPoint(10, 20))) ??
                                throw new InvalidOperationException("The test Backend did not return the first element.");
        using var secondSource = await visualService.AcquireAnchorAsync(VisualElementLocator.FromPoint(new PixelPoint(30, 40))) ??
                                 throw new InvalidOperationException("The test Backend did not return the second element.");

        using var firstDestination = await visualService.MoveAnchorAsync(visualState, firstSource);
        backend.ShouldFailAdoption = true;
        Assert.ThrowsAsync<RpcRemoteException>(async () => await visualService.MoveAnchorAsync(visualState, secondSource));
        using var movedCapture = await firstDestination.Context.CaptureAnchorAsync(firstDestination);
        using var remainingCapture = await secondSource.Context.CaptureAnchorAsync(secondSource);

        Assert.Multiple(() =>
        {
            Assert.That(firstSource.IsClosed, Is.True);
            Assert.That(firstDestination.IsClosed, Is.False);
            Assert.That(secondSource.IsClosed, Is.False);
            Assert.That(ReadCapture(movedCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(ReadCapture(remainingCapture), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        });
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static byte[] ReadCapture(IVisualElementCapture capture)
    {
        var data = new byte[checked(capture.Stride * capture.Size.Height)];
        Marshal.Copy(capture.Data, data, 0, data.Length);
        return data;
    }

    private sealed class AsyncTestResource : IAsyncDisposable
    {
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        private int _disposeCount;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    // Supplies deterministic element identity and capture behavior. These tests cover RPC,
    // lifecycle, and scheduling; platform UIA/AX acquisition and adoption require platform validation.
    private sealed class TestBackend(
        TaskCompletionSource? queryStarted = null,
        ManualResetEventSlim? continueQuery = null,
        bool shouldUsePointIdentity = false) : IVisualElementBackend
    {
        public int AcquisitionCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ElementReleaseCount { get; private set; }
        public int InvokeCount { get; private set; }
        public string? LastSetText { get; private set; }
        public KeyGesture? LastKeyGesture { get; private set; }
        public VisualElementFields LastRequestedFields { get; private set; }
        public int LastMaxTextCharacters { get; private set; }
        public VisualElementLocator LastLocator { get; private set; }
        public VisualElementResolution LastResolution { get; private set; }
        public Exception? AcquisitionException { get; set; }
        public Exception? ElementQueryException { get; set; }
        public VisualElementQueryFailure? ElementQueryFailure { get; set; }
        public VisualElementType ElementType { get; set; } = VisualElementType.Label;
        public bool ShouldFailAdoption { get; set; }
        public Func<IVisualElementCapture> CaptureFactory { get; set; } = static () => new TestCapture();

        public VisualElementQueryResult Query(
            VisualElementRetention retention,
            VisualElementLocator locator,
            VisualElementResolution resolution = VisualElementResolution.Direct,
            VisualElementQueryRequest? request = null)
        {
            AcquisitionCount++;
            LastLocator = locator;
            LastResolution = resolution;
            if (AcquisitionException is { } acquisitionException)
            {
                AcquisitionException = null;
                throw acquisitionException;
            }
            queryStarted?.TrySetResult();
            if (continueQuery is not null && !continueQuery.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the blocked native query.");
            }

            var nativeIdentity = shouldUsePointIdentity && locator.Kind == VisualElementLocatorKind.Point ?
                $"{locator.Point.X},{locator.Point.Y}" :
                "root";
            var element = retention.Context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(
                retention,
                nativeIdentity,
                this,
                static (identity, backend) => new TestVisualElement(identity, backend));
            return element.Query(request ?? VisualElementQueryRequest.Default);
        }

        public void Dispose() => DisposeCount++;

        public void RecordElementRelease() => ElementReleaseCount++;

        public void RecordInvoke() => InvokeCount++;

        public void RecordSetText(string text) => LastSetText = text;

        public void RecordKeyGesture(KeyGesture keyGesture) => LastKeyGesture = keyGesture;

        public IVisualElementCapture CreateCapture() => CaptureFactory();

        public VisualElementQueryFailure? RecordQuery(VisualElementQueryRequest request)
        {
            LastRequestedFields = request.RequestedFields;
            LastMaxTextCharacters = request.MaxTextCharacters;
            if (ElementQueryException is { } elementQueryException)
            {
                ElementQueryException = null;
                throw elementQueryException;
            }

            return ElementQueryFailure;
        }
    }

    private sealed class TestVisualElement(VisualElementIdentity identity, TestBackend backend) : VisualElement(identity, "root")
    {
        private const string Text = "hello world";

        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
        {
            var failure = backend.RecordQuery(request);
            if (failure is not null)
            {
                return new VisualElementQueryResult(this, default, VisualElementFields.None, request.RequestedFields, failure);
            }

            return new(
                this,
                new VisualElementSnapshot(Id, backend.ElementType, VisualElementStates.None, "Root", Text, false, new PixelRect(0, 0, 100, 20), null, null),
                request.RequestedFields,
                VisualElementFields.None,
                null);
        }

        protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters, int maximumProbeCharacters) =>
            VisualElementTextReadResult.FromSuccess(Text, offset, maxCharacters);

        protected override IVisualElementCursor CreateEnumeratorCore(
            VisualElementRelation relation,
            VisualElementQueryRequest request,
            int offset,
            CancellationToken cancellationToken) =>
            EmptyVisualElementEnumerator.Shared;

        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult(backend.CreateCapture());

        protected override VisualElement AdoptCore(VisualElementRetention destinationRetention)
        {
            if (backend.ShouldFailAdoption)
            {
                backend.ShouldFailAdoption = false;
                throw new InvalidOperationException("The test Backend rejected visual-element adoption.");
            }

            var sourceIdentity = (VisualElementIdentity<string>)Identity;
            return destinationRetention.Context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(
                destinationRetention,
                sourceIdentity.Value,
                backend,
                static (identity, state) => new TestVisualElement(identity, state));
        }

        protected override void InvokeCore() => backend.RecordInvoke();

        protected override void SetTextCore(string text) => backend.RecordSetText(text);

        protected override void SendKeyGestureCore(KeyGesture keyGesture) => backend.RecordKeyGesture(keyGesture);

        protected override void ReleaseCore() => backend.RecordElementRelease();
    }

    private sealed class TestCapture : IVisualElementCapture
    {
        public PixelRect Bounds { get; }
        public PixelFormat Format { get; }
        public AlphaFormat AlphaFormat { get; }
        public nint Data => _data.AddrOfPinnedObject();
        public PixelSize Size { get; }
        public int Stride { get; }

        private GCHandle _data;

        public TestCapture() : this(
            [1, 2, 3, 4, 5, 6, 7, 8],
            new PixelSize(2, 1),
            8,
            AlphaFormat.Premul)
        {
        }

        public TestCapture(byte[] data, PixelSize size, int stride, AlphaFormat alphaFormat)
        {
            Bounds = new PixelRect(10, 20, size.Width, size.Height);
            Format = PixelFormat.Bgra8888;
            AlphaFormat = alphaFormat;
            Size = size;
            Stride = stride;
            _data = GCHandle.Alloc(data, GCHandleType.Pinned);
        }

        public void Dispose()
        {
            if (_data.IsAllocated) _data.Free();
        }
    }

    private sealed class TestTextSelectionMonitorFactory : ITextSelectionMonitorFactory
    {
        public int DisposeCount => _monitor?.DisposeCount ?? 0;
        public bool IsStarted => _monitor is not null;

        private ITextSelectionMonitorContext? _context;
        private TestTextSelectionMonitor? _monitor;
        private Func<TextSelectionObservation, CancellationToken, ValueTask>? _publish;

        public ITextSelectionMonitor Create(
            ITextSelectionMonitorContext context,
            int mainProcessId,
            TextSelectionMonitoringConfiguration configuration,
            Func<TextSelectionObservation, CancellationToken, ValueTask> publish)
        {
            _context = context;
            _publish = publish;
            _monitor = new TestTextSelectionMonitor();
            return _monitor;
        }

        public async Task PublishAsync(string text)
        {
            var context = _context ?? throw new InvalidOperationException("The test monitor was not started.");
            var publish = _publish ?? throw new InvalidOperationException("The test monitor was not started.");
            var source = await context.ExecuteAsync(
                (visualContext, backend, token) =>
                {
                    var retention = visualContext.CreateRetention();
                    var result = backend.Query(retention, VisualElementLocator.Focused) ??
                                 throw new InvalidOperationException("The test Backend did not return its focused element.");
                    return new TextSelectionSource(retention, result);
                });
            await publish(new TextSelectionObservation(context, text, source), CancellationToken.None);
        }
    }

    private sealed class TestTextSelectionMonitor : ITextSelectionMonitor
    {
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        private int _disposeCount;

        public ValueTask UpdateConfigurationAsync(
            TextSelectionMonitoringConfiguration configuration,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestAutomationNotificationSink(TaskCompletionSource<TextSelectionObservedNotification> received) : IAutomationHostNotificationRpc
    {
        public ValueTask TextSelectionObservedAsync(
            TextSelectionObservedNotification notification,
            CancellationToken cancellationToken = default)
        {
            received.TrySetResult(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTextSelectionObserver : IObserver<TextSelectionData>
    {
        public TaskCompletionSource<TextSelectionData> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => Received.TrySetException(error);

        public void OnNext(TextSelectionData value) => Received.TrySetResult(value);
    }

    private sealed class TestHostConnectionSource(RpcConnection current) : IHostConnectionSource
    {
        public RpcConnection Current { get; set; } = current;

        public ValueTask<RpcConnection> GetConnectionAsync(
            ProcessRole role,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);

        public async IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(
            ProcessRole role,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class WatchingTestHostConnectionSource(RpcConnection current) : IHostConnectionSource
    {
        public ValueTask<RpcConnection> GetConnectionAsync(
            ProcessRole role,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(current);

        public async IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(
            ProcessRole role,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return current;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TestConnectionPair : IAsyncDisposable
    {
        public RpcConnection Server { get; }
        public RpcConnection Client { get; }

        private TestConnectionPair(RpcConnection server, RpcConnection client)
        {
            Server = server;
            Client = client;
        }

        public static async Task<TestConnectionPair> CreateAsync()
        {
            var pipeName = $"evat.{Guid.NewGuid():N}"[..13];
            var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waitForConnection = serverStream.WaitForConnectionAsync();
            var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await clientStream.ConnectAsync(5000);
            await waitForConnection;

            var options = new RpcConnectionOptions { RequireHandshake = false };
            return new TestConnectionPair(
                new RpcConnection(serverStream, isServer: true, options),
                new RpcConnection(clientStream, isServer: false, options));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
