using Avalonia;
using Everywhere.Automation.TestApp;
using Everywhere.Windows.Automation;

namespace Everywhere.Automation.Windows.Tests;

public sealed class ControlledTestAppTests
{
    [TestCase("winforms")]
    [TestCase("avalonia")]
    [TestCase("cefsharp")]
    [Explicit("Launches visible controlled target processes and requires an interactive Windows desktop.")]
    [Platform("Win")]
    [Category("ControlledTestApp")]
    public async Task MoveNext_WhenControlledTargetIsReady_AdvancesOneRevision(string backend)
    {
        var executablePath = GetExecutablePath(backend);
        Assert.That(File.Exists(executablePath), Is.True, $"Build the '{backend}' TestApp before running this tier.");

        var (controller, ready) = await TestAppProcessController.StartAsync(
            executablePath,
            "chat",
            42,
            TimeSpan.FromSeconds(30));
        await using (controller)
        {
            await controller.SendAsync(new TestAppCommand(TestAppCommandKind.MoveNext));
            var advanced = await controller.ReadStatusAsync();

            Assert.Multiple(() =>
            {
                Assert.That(ready.Kind, Is.EqualTo(TestAppStatusKind.Ready));
                Assert.That(ready.ProcessId, Is.EqualTo(controller.Process.Id));
                Assert.That(ready.Roots, Has.Count.EqualTo(1));
                Assert.That(ready.Roots[0].NativeHandle, Is.Not.Zero);
                Assert.That(ready.Anchors, Is.Not.Empty);
                Assert.That(ready.Anchors[0].RootIndex, Is.Zero);
                Assert.That(ready.Anchors[0].NativeId, Is.Not.Empty);
                Assert.That(advanced.Kind, Is.EqualTo(TestAppStatusKind.Advanced));
                Assert.That(advanced.Step, Is.EqualTo(1));
                Assert.That(advanced.Revision, Is.EqualTo(1));
                Assert.That(advanced.Anchors, Is.Not.Empty);
            });
        }
    }

    [Test]
    [Explicit("Launches a visible WinForms target and exercises the production Windows UIA reader.")]
    [Platform("Win")]
    [Category("ControlledTestApp")]
    public async Task QueryScope_WhenWinFormsTargetIsReady_UsesProductionUiaRuntime()
    {
        var executablePath = GetExecutablePath("winforms");
        Assert.That(File.Exists(executablePath), Is.True, "Build the WinForms TestApp before running this tier.");

        var (controller, ready) = await TestAppProcessController.StartAsync(
            executablePath,
            "chat",
            42,
            TimeSpan.FromSeconds(30));
        await using (controller)
        {
            var rootHandle = ready.Roots.Single().NativeHandle;
            var nativeRootHandle = (nint)rootHandle;
            await using var runtime = new WindowsVisualContextRuntime(VisualContextRuntimeOptions.Default);
            using var visualContext = new VisualContext(runtime);
            var options = new VisualContextScopeOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2));
            await using var scope = await runtime.EnterScopeAsync(visualContext, options);
            var queryRequest = new VisualElementQueryRequest(
                VisualElementFields.Id |
                VisualElementFields.Type |
                VisualElementFields.Name |
                VisualElementFields.Bounds |
                VisualElementFields.ProcessId |
                VisualElementFields.NativeWindowHandle,
                256);

            var root = await scope.QueryAsync(VisualElementLocator.FromNativeWindow(nativeRootHandle), queryRequest)
                ?? throw new InvalidOperationException("The scoped Windows reader did not resolve the target HWND.");
            await using var children = await root.Element.CreateEnumeratorAsync(
                VisualElementRelation.Child,
                new VisualElementEnumerationOptions(queryRequest));

            Assert.That(await children.HasMoreAsync(), Is.True);
            Assert.That(await children.MoveNextAsync(), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(root.IsSuccess, Is.True);
                Assert.That(root.IsPartial, Is.False);
                Assert.That(root.Snapshot.Id, Does.StartWith("uia:"));
                Assert.That(root.Snapshot.Type, Is.EqualTo(VisualElementType.TopLevel));
                Assert.That(root.Snapshot.ProcessId, Is.EqualTo(controller.Process.Id));
                Assert.That(root.Snapshot.NativeWindowHandle, Is.EqualTo(nativeRootHandle));
                Assert.That(children.Current.IsSuccess, Is.True);
                Assert.That(children.Current.Snapshot.Id, Is.Not.Empty);
                Assert.That(scope.PlatformOperationCount, Is.EqualTo(4));
            });

            await using var parents = await root.Element.CreateEnumeratorAsync(VisualElementRelation.Parent, new VisualElementEnumerationOptions(queryRequest));
            Assert.That(await parents.MoveNextAsync(), Is.True);
            var screen = parents.Current;
            Assert.That(await parents.MoveNextAsync(), Is.False);

            Assert.Multiple(() =>
            {
                Assert.That(screen.Element, Is.TypeOf<ScreenVisualElement>());
                Assert.That(screen.Snapshot.Id, Does.StartWith("screen:"));
                Assert.That(screen.Snapshot.Type, Is.EqualTo(VisualElementType.Screen));
                Assert.That(screen.AvailableFields.HasFlag(VisualElementFields.Bounds), Is.True);
                Assert.That(screen.MissingFields.HasFlag(VisualElementFields.ProcessId), Is.True);
                Assert.That(screen.MissingFields.HasFlag(VisualElementFields.NativeWindowHandle), Is.True);
                Assert.That(screen.Snapshot.ProcessId, Is.Null);
                Assert.That(screen.Snapshot.NativeWindowHandle, Is.Null);
            });

            var screenBounds = screen.Snapshot.Bounds ?? throw new InvalidOperationException("The composed Screen did not expose bounds.");
            var screenAtPoint = await scope.QueryAsync(
                VisualElementLocator.FromScreenPoint(new PixelPoint(screenBounds.X + screenBounds.Width / 2, screenBounds.Y + screenBounds.Height / 2)),
                queryRequest) ?? throw new InvalidOperationException("The scoped Windows reader did not resolve the Screen at the requested point.");
            var primaryScreen = await scope.QueryAsync(VisualElementLocator.PrimaryScreen, queryRequest)
                ?? throw new InvalidOperationException("The scoped Windows reader did not resolve the primary Screen.");
            Assert.Multiple(() =>
            {
                Assert.That(screenAtPoint.Snapshot.Id, Is.EqualTo(screen.Snapshot.Id));
                Assert.That(primaryScreen.Element, Is.TypeOf<ScreenVisualElement>());
                Assert.That(primaryScreen.Snapshot.Type, Is.EqualTo(VisualElementType.Screen));
            });

            var foundOriginalWindow = false;
            await using var windows = await screen.Element.CreateEnumeratorAsync(VisualElementRelation.Child, new VisualElementEnumerationOptions(queryRequest));
            for (var index = 0; index < 256 && await windows.MoveNextAsync(); index++)
            {
                if (windows.Current.Snapshot.NativeWindowHandle == nativeRootHandle)
                {
                    foundOriginalWindow = true;
                    break;
                }
            }

            Assert.That(foundOriginalWindow, Is.True, "The composed Screen children did not contain the originating top-level window.");
            using var capture = await screen.Element.CaptureAsync();
            Assert.Multiple(() =>
            {
                Assert.That(capture.Data, Is.Not.Zero);
                Assert.That(capture.Size.Width, Is.GreaterThan(0));
                Assert.That(capture.Size.Height, Is.GreaterThan(0));
            });
        }
    }

    [TestCase("winforms")]
    [TestCase("avalonia")]
    [TestCase("cefsharp")]
    [Explicit("Launches a visible controlled target and probes retained UIA Element use across dedicated MTA workers.")]
    [Platform("Win")]
    [Category("ControlledTestApp")]
    public async Task RetainedElement_WhenDispatchedOnAnotherMtaWorker_RemainsUsable(string backend)
    {
        var executablePath = GetExecutablePath(backend);
        Assert.That(File.Exists(executablePath), Is.True, $"Build the '{backend}' TestApp before running this tier.");

        var (controller, ready) = await TestAppProcessController.StartAsync(executablePath, "chat", 42, TimeSpan.FromSeconds(30));
        await using (controller)
        {
            var runtimeOptions = new VisualContextRuntimeOptions(2, 2, 2, 8, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            await using var runtime = new WindowsVisualContextRuntime(runtimeOptions);
            using var visualContext = new VisualContext(runtime);
            var blocks = new List<RuntimeWorkerBlock>();
            try
            {
                Assert.That(await runtime.RunAsync(() => 0), Is.Zero);
                await WaitForIdleWorkersAsync(runtime, 2);
                var initialBlock = await RuntimeWorkerBlock.StartAsync(runtime);
                blocks.Add(initialBlock);
                var originWorkerIndex = runtime.GetSnapshot().Workers.Single(worker => worker.Index != initialBlock.WorkerIndex).Index;
                var options = new VisualContextScopeOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
                await using var scope = await runtime.EnterScopeAsync(visualContext, options);
                var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Name | VisualElementFields.ProcessId | VisualElementFields.NativeWindowHandle, 256);
                var rootHandle = (nint)ready.Roots.Single().NativeHandle;
                var root = await scope.QueryAsync(VisualElementLocator.FromNativeWindow(rootHandle), request)
                    ?? throw new InvalidOperationException("The scoped Windows reader did not resolve the controlled target HWND.");

                await initialBlock.DisposeAsync();
                blocks.Remove(initialBlock);
                var targetBlock = await RuntimeWorkerBlock.StartAsync(runtime);
                blocks.Add(targetBlock);
                if (targetBlock.WorkerIndex != originWorkerIndex)
                {
                    var secondBlock = await RuntimeWorkerBlock.StartAsync(runtime, targetBlock.WorkerIndex);
                    blocks.Add(secondBlock);
                    await targetBlock.DisposeAsync();
                    blocks.Remove(targetBlock);
                    targetBlock = secondBlock;
                }

                Assert.That(targetBlock.WorkerIndex, Is.EqualTo(originWorkerIndex));
                var refreshed = await root.Element.QueryAsync(request);
                await using var children = await root.Element.CreateEnumeratorAsync(VisualElementRelation.Child, new VisualElementEnumerationOptions(request));
                var hasChild = await children.MoveNextAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(refreshed.IsSuccess, Is.True);
                    Assert.That(refreshed.Snapshot.Id, Is.EqualTo(root.Snapshot.Id));
                    Assert.That(refreshed.Snapshot.ProcessId, Is.EqualTo(controller.Process.Id));
                    Assert.That(hasChild, Is.True);
                    Assert.That(children.Current.IsSuccess, Is.True);
                });
            }
            finally
            {
                for (var index = blocks.Count - 1; index >= 0; index--)
                {
                    await blocks[index].DisposeAsync();
                }
            }
        }
    }

    private sealed class RuntimeWorkerBlock : IAsyncDisposable
    {
        internal int WorkerIndex { get; }

        private readonly ManualResetEventSlim _releaseOperation;
        private readonly Task _operation;
        private bool _isDisposed;

        private RuntimeWorkerBlock(int workerIndex, ManualResetEventSlim releaseOperation, Task operation)
        {
            WorkerIndex = workerIndex;
            _releaseOperation = releaseOperation;
            _operation = operation;
        }

        internal static async ValueTask<RuntimeWorkerBlock> StartAsync(WindowsVisualContextRuntime runtime, params IReadOnlyList<int> excludedWorkerIndices)
        {
            var operationStarted = new ManualResetEventSlim();
            var releaseOperation = new ManualResetEventSlim();
            var operation = runtime.RunAsync(
                () =>
                {
                    operationStarted.Set();
                    releaseOperation.Wait();
                    return 0;
                }).AsTask();
            if (!operationStarted.Wait(TimeSpan.FromSeconds(5)))
            {
                releaseOperation.Set();
                await operation;
                operationStarted.Dispose();
                releaseOperation.Dispose();
                throw new TimeoutException("A Runtime worker did not enter the controlled blocking operation.");
            }

            operationStarted.Dispose();
            try
            {
                var worker = runtime.GetSnapshot().Workers.Single(worker => worker.State == VisualContextRuntimeWorkerState.Running && !excludedWorkerIndices.Contains(worker.Index));
                return new RuntimeWorkerBlock(worker.Index, releaseOperation, operation);
            }
            catch
            {
                releaseOperation.Set();
                await operation;
                releaseOperation.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _releaseOperation.Set();
            await _operation;
            _releaseOperation.Dispose();
        }
    }

    private static async Task WaitForIdleWorkersAsync(WindowsVisualContextRuntime runtime, int workerCount)
    {
        var startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var snapshot = runtime.GetSnapshot();
            if (snapshot.Workers.Any(worker => worker.State == VisualContextRuntimeWorkerState.Faulted))
            {
                Assert.Fail($"A Windows UIA Runtime worker faulted during initialization: {string.Join(", ", snapshot.Workers.Select(worker => $"{worker.Index}:{worker.State}:{worker.Status}"))}.");
            }

            if (snapshot.Workers.Count == workerCount && snapshot.Workers.All(worker => worker.State == VisualContextRuntimeWorkerState.Idle))
            {
                return;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp) >= TimeSpan.FromSeconds(5))
            {
                Assert.Fail($"Windows UIA Runtime workers did not become idle: {string.Join(", ", snapshot.Workers.Select(worker => $"{worker.Index}:{worker.State}"))}.");
            }

            await Task.Delay(10);
        }
    }

    private static string GetExecutablePath(string backend) => backend switch
    {
        "winforms" => ControlledTestAppPaths.WinForms,
        "avalonia" => ControlledTestAppPaths.Avalonia,
        "cefsharp" => ControlledTestAppPaths.CefSharp,
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
    };
}
