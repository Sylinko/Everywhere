using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Everywhere.Automation.TestApp;
using Interop.UIAutomationClient;

namespace Everywhere.Automation.Windows.Tests;

/// <summary>
/// Preserves empirical probes for UI Automation behavior that is not fully specified by the public API contract.
/// </summary>
/// <remarks>
/// These probes intentionally report pointer identity, RCW reuse, managed allocation, worker timeout behavior, and concurrent progress instead of asserting implementation-specific values. Run them on every supported Windows baseline before changing UIA worker or client ownership.
/// </remarks>
[NonParallelizable]
public sealed class UIAutomationBehaviorProbeTests
{
    private const int IterationCount = 512;
    private const uint ShortTimeoutMilliseconds = 400;
    private const uint LongTimeoutMilliseconds = 3500;

    [Test]
    [Explicit("Launches a visible WinForms target and reports UIA RCW, COM identity, RuntimeId, and allocation behavior.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task SameElement_WhenQueriedRepeatedlyAcrossClients_ReportsIdentityAndAllocationShape()
    {
        var (controller, ready) = await StartWinFormsTargetAsync();
        await using (controller)
        await using (var workerA = await UIAutomationProbeWorker.CreateAsync())
        await using (var workerB = await UIAutomationProbeWorker.CreateAsync())
        {
            var nativeHandle = (nint)ready.Roots.Single().NativeHandle;
            var elementA1 = await workerA.InvokeAsync(automation => automation.ElementFromHandle(nativeHandle));
            var elementA2 = await workerA.InvokeAsync(automation => automation.ElementFromHandle(nativeHandle));
            var elementB = await workerB.InvokeAsync(automation => automation.ElementFromHandle(nativeHandle));
            var cacheRequestB = await workerB.InvokeAsync(CreateCacheRequest);
            var refreshedByB = await workerB.InvokeAsync(_ => elementA1.BuildUpdatedCache(cacheRequestB));
            var sameClientElementFromHandle = await workerA.InvokeAsync(automation => ProbeElementFromHandle(automation, nativeHandle));
            var secondClientElementFromHandle = await workerB.InvokeAsync(automation => ProbeElementFromHandle(automation, nativeHandle));
            var sameClientParent = await workerA.InvokeAsync(automation => ProbeParent(automation, nativeHandle));
            var secondClientParent = await workerB.InvokeAsync(automation => ProbeParent(automation, nativeHandle));
            var report = new ElementIdentityProbeReport(
                CreateEnvironmentReport(),
                DescribeElement(elementA1),
                DescribeElement(elementA2),
                DescribeElement(elementB),
                DescribeElement(refreshedByB),
                ReferenceEquals(elementA1, elementA2),
                ReferenceEquals(elementA1, elementB),
                ReferenceEquals(elementA1, refreshedByB),
                await workerA.InvokeAsync(automation => automation.CompareElements(elementA1, elementA2) != 0),
                await workerA.InvokeAsync(automation => automation.CompareElements(elementA1, elementB) != 0),
                await workerA.InvokeAsync(automation => automation.CompareElements(elementA1, refreshedByB) != 0),
                sameClientElementFromHandle,
                secondClientElementFromHandle,
                sameClientParent,
                secondClientParent);

            WriteReport(report);
            Assert.Multiple(() =>
            {
                Assert.That(GetRuntimeId(elementA2), Is.EqualTo(GetRuntimeId(elementA1)));
                Assert.That(GetRuntimeId(elementB), Is.EqualTo(GetRuntimeId(elementA1)));
                Assert.That(GetRuntimeId(refreshedByB), Is.EqualTo(GetRuntimeId(elementA1)));
                Assert.That(report.SameClientCompareElements, Is.True);
                Assert.That(report.CrossClientCompareElements, Is.True);
                Assert.That(report.CrossClientUpdatedCompareElements, Is.True);
            });
        }
    }

    [Test]
    [Explicit("Launches a visible WinForms target, blocks its UI thread, and reports whether BuildUpdatedCache follows the executing worker rather than the Element origin worker.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task BuildUpdatedCache_WhenElementOriginDiffers_ReportsExecutingWorkerTimeout()
    {
        var (controller, ready) = await StartWinFormsTargetAsync();
        await using (controller)
        await using (var workerA = await UIAutomationProbeWorker.CreateAsync())
        await using (var workerB = await UIAutomationProbeWorker.CreateAsync())
        {
            var nativeHandle = (nint)ready.Roots.Single().NativeHandle;
            var nativeId = ready.Anchors.Single(anchor => anchor.Key == "input").NativeId;
            var elementA = await workerA.InvokeAsync(automation => ResolveProviderElement(automation, nativeHandle, nativeId));
            var providerNativeHandle = await workerA.InvokeAsync(_ => elementA.CurrentNativeWindowHandle);
            var cacheRequestB = await workerB.InvokeAsync(CreateCacheRequest);

            await ConfigureTimeoutsAsync(workerA, LongTimeoutMilliseconds, LongTimeoutMilliseconds);
            await ConfigureTimeoutsAsync(workerB, LongTimeoutMilliseconds, ShortTimeoutMilliseconds);
            var shortExecutingWorker = await RunWhileUiThreadSuspendedAsync(controller, () => MeasureCallAsync(workerB, _ => elementA.BuildUpdatedCache(cacheRequestB)));

            await ConfigureTimeoutsAsync(workerA, LongTimeoutMilliseconds, ShortTimeoutMilliseconds);
            await ConfigureTimeoutsAsync(workerB, LongTimeoutMilliseconds, LongTimeoutMilliseconds);
            var shortElementOriginWorker = await RunWhileUiThreadSuspendedAsync(controller, () => MeasureCallAsync(workerB, _ => elementA.BuildUpdatedCache(cacheRequestB)));

            await ConfigureTimeoutsAsync(workerB, ShortTimeoutMilliseconds, LongTimeoutMilliseconds);
            var shortConnection = await RunWhileUiThreadSuspendedAsync(controller, async () =>
            {
                var request = await workerB.InvokeAsync(CreateCacheRequest);
                return await MeasureCallAsync(workerB, automation => automation.ElementFromHandleBuildCache(providerNativeHandle, request));
            });

            await ConfigureTimeoutsAsync(workerB, LongTimeoutMilliseconds, ShortTimeoutMilliseconds);
            var shortTransaction = await RunWhileUiThreadSuspendedAsync(controller, async () =>
            {
                var request = await workerB.InvokeAsync(CreateCacheRequest);
                return await MeasureCallAsync(workerB, automation => automation.ElementFromHandleBuildCache(providerNativeHandle, request));
            });

            var report = new WorkerTimeoutProbeReport(
                CreateEnvironmentReport(),
                ShortTimeoutMilliseconds,
                LongTimeoutMilliseconds,
                shortExecutingWorker,
                shortElementOriginWorker,
                ClassifyWorkerTimeout(shortExecutingWorker, shortElementOriginWorker),
                shortConnection,
                shortTransaction,
                ClassifyAcquisitionTimeout(shortConnection, shortTransaction));
            WriteReport(report);

            Assert.Multiple(() =>
            {
                Assert.That(shortExecutingWorker.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                Assert.That(shortElementOriginWorker.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                Assert.That(shortConnection.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                Assert.That(shortTransaction.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
            });
        }
    }

    [Test]
    [Explicit("Launches two visible WinForms targets and compares concurrent UIA progress with shared and worker-local clients.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task ConcurrentCalls_WhenOneProviderIsBlocked_ReportsSharedClientIsolation()
    {
        var (blockedController, blockedReady) = await StartWinFormsTargetAsync(42);
        await using (blockedController)
        {
            var (responsiveController, responsiveReady) = await StartWinFormsTargetAsync(43);
            await using (responsiveController)
            {
                var workerLocalClients = await ProbeConcurrentProgressAsync(blockedController, blockedReady, responsiveReady, false);
                var sharedClient = await ProbeConcurrentProgressAsync(blockedController, blockedReady, responsiveReady, true);
                WriteReport(new ConcurrentClientProbeReport(CreateEnvironmentReport(), workerLocalClients, sharedClient));

                Assert.Multiple(() =>
                {
                    Assert.That(workerLocalClients.BlockedCall.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                    Assert.That(workerLocalClients.ResponsiveCall.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                    Assert.That(sharedClient.BlockedCall.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                    Assert.That(sharedClient.ResponsiveCall.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
                });
            }
        }
    }

    [Test]
    [Explicit("Launches a visible WinForms target and reports RuntimeId behavior while a retained wrapper outlives native control reconstruction.")]
    [Platform("Win")]
    [Category("UIAutomationProbe")]
    public async Task RuntimeId_WhenNativeControlIsRebuiltWithOldWrapperRetained_ReportsIncarnationBehavior()
    {
        var (controller, ready) = await StartWinFormsTargetAsync();
        await using (controller)
        await using (var worker = await UIAutomationProbeWorker.CreateAsync())
        {
            var nativeHandle = (nint)ready.Roots.Single().NativeHandle;
            var nativeId = ready.Anchors.Single(anchor => anchor.Key == "input").NativeId;
            var root = await worker.InvokeAsync(automation => automation.ElementFromHandle(nativeHandle));
            var retained = await worker.InvokeAsync(automation => FindByAutomationId(automation, root, nativeId));
            var duplicate = await worker.InvokeAsync(automation => FindByAutomationId(automation, root, nativeId));
            var observations = new List<RuntimeIdRebuildObservation>();
            var retainedRuntimeId = GetRuntimeId(retained);
            var duplicateRuntimeId = GetRuntimeId(duplicate);
            var initialRetainedElement = DescribeElement(retained);
            var initialDuplicateElement = DescribeElement(duplicate);
            var initialCompareElements = await worker.InvokeAsync(automation => automation.CompareElements(retained, duplicate) != 0);

            for (var step = 1; step <= 8; step++)
            {
                await controller.SendAsync(new TestAppCommand(TestAppCommandKind.MoveNext));
                var advanced = await controller.ReadStatusAsync();
                var current = await worker.InvokeAsync(automation => FindByAutomationId(automation, root, nativeId));
                var currentRuntimeId = GetRuntimeId(current);
                var retainedAvailability = await worker.InvokeAsync(_ => ProbeElementAvailability(retained));
                var comparison = await worker.InvokeAsync(automation => ProbeComparison(automation, retained, current));
                observations.Add(new RuntimeIdRebuildObservation(
                    step,
                    advanced.Revision,
                    retainedRuntimeId,
                    currentRuntimeId,
                    retainedRuntimeId == currentRuntimeId,
                    FormatIUnknownIdentity(current),
                    retainedAvailability,
                    comparison));
            }

            var report = new RuntimeIdIncarnationProbeReport(
                CreateEnvironmentReport(),
                nativeId,
                initialRetainedElement,
                initialDuplicateElement,
                ReferenceEquals(retained, duplicate),
                initialCompareElements,
                observations);
            WriteReport(report);

            Assert.Multiple(() =>
            {
                Assert.That(duplicateRuntimeId, Is.EqualTo(retainedRuntimeId));
                Assert.That(report.InitialCompareElements, Is.True);
                Assert.That(observations, Has.Count.EqualTo(8));
            });
        }
    }

    private static async Task<ConcurrentProgressObservation> ProbeConcurrentProgressAsync(
        TestAppProcessController blockedController,
        TestAppStatus blockedReady,
        TestAppStatus responsiveReady,
        bool shouldShareClient)
    {
        await using var ownerWorker = await UIAutomationProbeWorker.CreateAsync();
        var sharedAutomation = shouldShareClient ? await ownerWorker.InvokeAsync(automation => automation) : null;
        await using var blockedWorker = await UIAutomationProbeWorker.CreateAsync(sharedAutomation);
        await using var responsiveWorker = await UIAutomationProbeWorker.CreateAsync(sharedAutomation);
        await ConfigureTimeoutsAsync(blockedWorker, LongTimeoutMilliseconds, LongTimeoutMilliseconds);
        if (!shouldShareClient)
        {
            await ConfigureTimeoutsAsync(responsiveWorker, LongTimeoutMilliseconds, LongTimeoutMilliseconds);
        }

        var blockedHandle = (nint)blockedReady.Roots.Single().NativeHandle;
        var responsiveHandle = (nint)responsiveReady.Roots.Single().NativeHandle;
        var blockedNativeId = blockedReady.Anchors.Single(anchor => anchor.Key == "input").NativeId;
        var blockedElement = await blockedWorker.InvokeAsync(automation => ResolveProviderElement(automation, blockedHandle, blockedNativeId));
        var blockedCache = await blockedWorker.InvokeAsync(CreateCacheRequest);
        var responsiveCache = await responsiveWorker.InvokeAsync(CreateCacheRequest);
        await blockedController.SuspendUiThreadAsync();
        using var blockedStarted = new ManualResetEventSlim();
        using var rescueCancellation = new CancellationTokenSource();
        var hasSentResume = 0;
        async Task ResumeOnceAsync()
        {
            if (Interlocked.Exchange(ref hasSentResume, 1) == 0)
            {
                await blockedController.SendAsync(new TestAppCommand(TestAppCommandKind.ResumeUiThread));
            }
        }

        var rescueTask = ResumeAfterDelayAsync(ResumeOnceAsync, rescueCancellation.Token);
        try
        {
            var blockedTask = MeasureCallAsync(blockedWorker, _ =>
            {
                blockedStarted.Set();
                return blockedElement.BuildUpdatedCache(blockedCache);
            });
            Assert.That(blockedStarted.Wait(TimeSpan.FromSeconds(2)), Is.True, "The blocked UIA probe did not start.");
            await Task.Delay(200);
            var wasBlockedCallPendingAtResponsiveStart = !blockedTask.IsCompleted;
            var responsiveTask = MeasureCallAsync(responsiveWorker, automation => automation.ElementFromHandleBuildCache(responsiveHandle, responsiveCache));
            var firstCompletion = await Task.WhenAny(blockedTask, responsiveTask, Task.Delay(TimeSpan.FromSeconds(1.5)));
            var didResponsiveCallCompleteWhileBlocked = ReferenceEquals(firstCompletion, responsiveTask) && wasBlockedCallPendingAtResponsiveStart;
            await ResumeOnceAsync();
            var resumed = await blockedController.ReadStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(resumed.Kind, Is.EqualTo(TestAppStatusKind.UiThreadResumed));
            return new ConcurrentProgressObservation(
                shouldShareClient,
                wasBlockedCallPendingAtResponsiveStart,
                didResponsiveCallCompleteWhileBlocked,
                await blockedTask.WaitAsync(TimeSpan.FromSeconds(8)),
                await responsiveTask.WaitAsync(TimeSpan.FromSeconds(8)));
        }
        finally
        {
            rescueCancellation.Cancel();
            await ResumeOnceAsync();
            await rescueTask;
        }
    }

    private static async Task<CallObservation> RunWhileUiThreadSuspendedAsync(TestAppProcessController controller, Func<Task<CallObservation>> operation)
    {
        await controller.SuspendUiThreadAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var rescueCancellation = new CancellationTokenSource();
        var hasSentResume = 0;
        async Task ResumeOnceAsync()
        {
            if (Interlocked.Exchange(ref hasSentResume, 1) == 0)
            {
                await controller.SendAsync(new TestAppCommand(TestAppCommandKind.ResumeUiThread));
            }
        }

        var rescueTask = ResumeAfterDelayAsync(ResumeOnceAsync, rescueCancellation.Token);
        try
        {
            return await operation().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            rescueCancellation.Cancel();
            await ResumeOnceAsync();
            var resumed = await controller.ReadStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(resumed.Kind, Is.EqualTo(TestAppStatusKind.UiThreadResumed));
            await rescueTask;
        }
    }

    private static async Task ResumeAfterDelayAsync(Func<Task> resumeAsync, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
            await resumeAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Task<CallObservation> MeasureCallAsync(UIAutomationProbeWorker worker, Func<IUIAutomation2, object> operation) => worker.InvokeAsync(automation =>
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var value = operation(automation);
            stopwatch.Stop();
            return new CallObservation(stopwatch.Elapsed, true, value.GetType().FullName, null, null);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new CallObservation(stopwatch.Elapsed, false, null, exception.GetType().FullName, $"0x{exception.HResult:X8}");
        }
    });

    private static async Task ConfigureTimeoutsAsync(UIAutomationProbeWorker worker, uint connectionTimeout, uint transactionTimeout) => await worker.InvokeAsync(automation =>
    {
        automation.ConnectionTimeout = connectionTimeout;
        automation.TransactionTimeout = transactionTimeout;
        return 0;
    });

    private static RepeatedQueryObservation ProbeElementFromHandle(IUIAutomation2 automation, nint nativeHandle) => ProbeRepeatedly(() => automation.ElementFromHandle(nativeHandle));

    private static RepeatedQueryObservation ProbeParent(IUIAutomation2 automation, nint nativeHandle)
    {
        var root = automation.ElementFromHandle(nativeHandle);
        var child = automation.ContentViewWalker.GetFirstChildElement(root) ?? root;
        return ProbeRepeatedly(() => automation.ContentViewWalker.GetParentElement(child));
    }

    private static RepeatedQueryObservation ProbeRepeatedly(Func<IUIAutomationElement> query)
    {
        _ = query();
        var managedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var unknownIdentities = new HashSet<nint>();
        var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < IterationCount; index++)
        {
            var element = query();
            managedObjects.Add(element);
            unknownIdentities.Add(GetIUnknownPointer(element));
            runtimeIds.Add(GetRuntimeId(element));
        }

        stopwatch.Stop();
        return new RepeatedQueryObservation(
            IterationCount,
            stopwatch.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            managedObjects.Count,
            unknownIdentities.Count,
            runtimeIds.Count);
    }

    private static IUIAutomationElement ResolveProviderElement(IUIAutomation2 automation, nint nativeHandle, string automationId)
    {
        var root = automation.ElementFromHandle(nativeHandle);
        return FindByAutomationId(automation, root, automationId);
    }

    private static IUIAutomationElement FindByAutomationId(IUIAutomation2 automation, IUIAutomationElement root, string automationId)
    {
        var condition = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_AutomationIdPropertyId, automationId);
        return root.FindFirst(TreeScope.TreeScope_Descendants, condition)
            ?? throw new InvalidOperationException($"UI Automation did not resolve controlled anchor '{automationId}'.");
    }

    private static IUIAutomationCacheRequest CreateCacheRequest(IUIAutomation2 automation)
    {
        var request = automation.CreateCacheRequest();
        request.TreeScope = TreeScope.TreeScope_Element;
        request.AddProperty(UIA_PropertyIds.UIA_RuntimeIdPropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_NamePropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_ProcessIdPropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        request.AddProperty(UIA_PropertyIds.UIA_ValueValuePropertyId);
        return request;
    }

    private static ElementObservation DescribeElement(IUIAutomationElement element) => new(GetRuntimeId(element), FormatIUnknownIdentity(element), Marshal.IsComObject(element), element.GetType().FullName);

    private static string GetRuntimeId(IUIAutomationElement element) => string.Join(',', element.GetRuntimeId());

    private static string FormatIUnknownIdentity(object value) => $"0x{GetIUnknownPointer(value):X}";

    private static nint GetIUnknownPointer(object value)
    {
        var pointer = Marshal.GetIUnknownForObject(value);
        try
        {
            return pointer;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static ElementAvailabilityObservation ProbeElementAvailability(IUIAutomationElement element)
    {
        try
        {
            return new ElementAvailabilityObservation(true, element.CurrentName, null, null);
        }
        catch (Exception exception)
        {
            return new ElementAvailabilityObservation(false, null, exception.GetType().FullName, $"0x{exception.HResult:X8}");
        }
    }

    private static ElementComparisonObservation ProbeComparison(IUIAutomation2 automation, IUIAutomationElement first, IUIAutomationElement second)
    {
        try
        {
            return new ElementComparisonObservation(true, automation.CompareElements(first, second) != 0, null, null);
        }
        catch (Exception exception)
        {
            return new ElementComparisonObservation(false, null, exception.GetType().FullName, $"0x{exception.HResult:X8}");
        }
    }

    private static string ClassifyWorkerTimeout(CallObservation shortExecutingWorker, CallObservation shortElementOriginWorker)
    {
        var executingWorkerWasShort = shortExecutingWorker.Elapsed < TimeSpan.FromMilliseconds(1500);
        var elementOriginWorkerWasShort = shortElementOriginWorker.Elapsed < TimeSpan.FromMilliseconds(1500);
        if (shortExecutingWorker.IsSuccess && shortElementOriginWorker.IsSuccess && shortExecutingWorker.Elapsed < TimeSpan.FromMilliseconds(50) && shortElementOriginWorker.Elapsed < TimeSpan.FromMilliseconds(50))
        {
            return "provider-call-did-not-block";
        }

        if (executingWorkerWasShort && !elementOriginWorkerWasShort)
        {
            return "executing-worker";
        }

        if (!executingWorkerWasShort && elementOriginWorkerWasShort)
        {
            return "element-origin-worker";
        }

        return executingWorkerWasShort && elementOriginWorkerWasShort ? "shared-or-short-independent-of-worker" : "ambiguous-or-timeout-not-observed";
    }

    private static string ClassifyAcquisitionTimeout(CallObservation shortConnection, CallObservation shortTransaction)
    {
        var connectionWasShort = shortConnection.Elapsed < TimeSpan.FromMilliseconds(1500);
        var transactionWasShort = shortTransaction.Elapsed < TimeSpan.FromMilliseconds(1500);
        if (connectionWasShort && !transactionWasShort)
        {
            return "connection-timeout";
        }

        if (!connectionWasShort && transactionWasShort)
        {
            return "transaction-timeout";
        }

        return connectionWasShort && transactionWasShort ? "both-or-shortest-timeout" : "ambiguous-or-timeout-not-observed";
    }

    private static async Task<(TestAppProcessController Controller, TestAppStatus Ready)> StartWinFormsTargetAsync(long seed = 42)
    {
        var executablePath = ControlledTestAppPaths.WinForms;
        Assert.That(File.Exists(executablePath), Is.True, "Build the WinForms TestApp before running this tier.");
        return await TestAppProcessController.StartAsync(executablePath, "chat", seed, TimeSpan.FromSeconds(30));
    }

    private static ProbeEnvironmentReport CreateEnvironmentReport() => new(
        Environment.OSVersion.VersionString,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Environment.Version.ToString());

    private static void WriteReport<T>(T report) => TestContext.Progress.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    private sealed record ProbeEnvironmentReport(string WindowsVersion, string OSDescription, string OSArchitecture, string ProcessArchitecture, string Framework, string RuntimeVersion);

    private sealed record ElementObservation(string RuntimeId, string IUnknownIdentity, bool IsComObject, string? RuntimeType);

    private sealed record RepeatedQueryObservation(int Iterations, TimeSpan Elapsed, long ManagedAllocatedBytesIncludingProbeBookkeeping, int DistinctManagedObjects, int DistinctIUnknownIdentities, int DistinctRuntimeIds);

    private sealed record ElementIdentityProbeReport(ProbeEnvironmentReport Environment, ElementObservation FirstSameClientElement, ElementObservation SecondSameClientElement, ElementObservation OtherClientElement, ElementObservation CrossClientUpdatedElement, bool SameClientReferenceEquals, bool CrossClientReferenceEquals, bool CrossClientUpdatedReferenceEquals, bool SameClientCompareElements, bool CrossClientCompareElements, bool CrossClientUpdatedCompareElements, RepeatedQueryObservation SameClientElementFromHandle, RepeatedQueryObservation OtherClientElementFromHandle, RepeatedQueryObservation SameClientParent, RepeatedQueryObservation OtherClientParent);

    private sealed record CallObservation(TimeSpan Elapsed, bool IsSuccess, string? ResultType, string? ExceptionType, string? HResult);

    private sealed record WorkerTimeoutProbeReport(ProbeEnvironmentReport Environment, uint ShortTimeoutMilliseconds, uint LongTimeoutMilliseconds, CallObservation ShortTimeoutOnExecutingWorker, CallObservation ShortTimeoutOnElementOriginWorker, string BuildUpdatedCacheBoundary, CallObservation AcquisitionWithShortConnectionTimeout, CallObservation AcquisitionWithShortTransactionTimeout, string AcquisitionBoundary);

    private sealed record ConcurrentProgressObservation(bool UsesSharedClient, bool WasBlockedCallPendingAtResponsiveStart, bool DidResponsiveCallCompleteWhileBlocked, CallObservation BlockedCall, CallObservation ResponsiveCall);

    private sealed record ConcurrentClientProbeReport(ProbeEnvironmentReport Environment, ConcurrentProgressObservation WorkerLocalClients, ConcurrentProgressObservation SharedClient);

    private sealed record ElementAvailabilityObservation(bool IsAvailable, string? Name, string? ExceptionType, string? HResult);

    private sealed record ElementComparisonObservation(bool DidComplete, bool? AreSame, string? ExceptionType, string? HResult);

    private sealed record RuntimeIdRebuildObservation(int Step, long Revision, string RetainedRuntimeId, string CurrentRuntimeId, bool IsRuntimeIdReused, string CurrentIUnknownIdentity, ElementAvailabilityObservation RetainedAvailability, ElementComparisonObservation Comparison);

    private sealed record RuntimeIdIncarnationProbeReport(ProbeEnvironmentReport Environment, string AutomationId, ElementObservation InitialRetainedElement, ElementObservation InitialDuplicateElement, bool InitialReferenceEquals, bool InitialCompareElements, IReadOnlyList<RuntimeIdRebuildObservation> Rebuilds);
}

/// <summary>
/// Runs raw UI Automation probe operations on one dedicated MTA thread, optionally sharing an existing UIA client.
/// </summary>
internal sealed class UIAutomationProbeWorker : IAsyncDisposable
{
    private readonly BlockingCollection<IUIAutomationProbeWorkItem> _workItems = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IUIAutomation2? _sharedAutomation;
    private readonly Thread _thread;
    private IUIAutomation2? _automation;
    private bool _isDisposed;

    private UIAutomationProbeWorker(IUIAutomation2? sharedAutomation)
    {
        _sharedAutomation = sharedAutomation;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UI Automation behavior probe",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>
    /// Creates a probe worker and waits until its MTA thread has acquired the requested UIA client.
    /// </summary>
    internal static async Task<UIAutomationProbeWorker> CreateAsync(IUIAutomation2? sharedAutomation = null)
    {
        var worker = new UIAutomationProbeWorker(sharedAutomation);
        await worker._started.Task;
        return worker;
    }

    /// <summary>
    /// Invokes one synchronous UIA operation on the dedicated MTA thread.
    /// </summary>
    internal Task<T> InvokeAsync<T>(Func<IUIAutomation2, T> operation)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var workItem = new UIAutomationProbeWorkItem<T>(operation);
        _workItems.Add(workItem);
        return workItem.Task;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        _workItems.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The UI Automation probe worker did not stop. A native UIA call may still be blocked.");
        }

        _workItems.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Run()
    {
        try
        {
            _automation = _sharedAutomation ?? new CUIAutomation8Class();
            _started.SetResult();
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                workItem.Execute(_automation);
            }
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            while (_workItems.TryTake(out var workItem))
            {
                workItem.Fail(exception);
            }
        }
        finally
        {
            _automation = null;
        }
    }

    private interface IUIAutomationProbeWorkItem
    {
        void Execute(IUIAutomation2 automation);

        void Fail(Exception exception);
    }

    private sealed class UIAutomationProbeWorkItem<T>(Func<IUIAutomation2, T> operation) : IUIAutomationProbeWorkItem
    {
        internal Task<T> Task => _completion.Task;

        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Execute(IUIAutomation2 automation)
        {
            try
            {
                _completion.SetResult(operation(automation));
            }
            catch (Exception exception)
            {
                _completion.SetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
