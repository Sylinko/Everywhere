using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Windows.Interop;

namespace Everywhere.Windows.Tests.Interop;

[TestFixture]
[NonParallelizable]
public sealed class Direct3D11ScreenCaptureTests
{
    [Test]
    public async Task Capture_WhenCompleted_KeepsMappedPixelsUntilDisposeAndExitsThread()
    {
        using var source = await CaptureSource.CreateAsync();
        var capture = await CaptureAsync(source);
        try
        {
            var thread = GetField<Thread>(capture, "_thread");
            var initial = ReadPixels(capture);
            await Task.Yield();
            Assert.Multiple(() =>
            {
                Assert.That(thread.IsAlive, Is.True);
                Assert.That(thread.GetApartmentState(), Is.EqualTo(ApartmentState.STA));
                Assert.That(capture.Data, Is.Not.EqualTo(nint.Zero));
                Assert.That(capture.Size, Is.EqualTo(new PixelSize(64, 48)));
                Assert.That(ReadPixels(capture), Is.EqualTo(initial));
                Assert.That(GetField<object?>(capture, "_framePool"), Is.Null);
                Assert.That(GetField<object?>(capture, "_pendingFrame"), Is.Null);
                Assert.That(GetField<object?>(capture, "_hostWindow"), Is.Null);
            });
            capture.Dispose();
            capture.Dispose();
            Assert.Multiple(() =>
            {
                Assert.That(thread.IsAlive, Is.False);
                Assert.That(capture.Data, Is.EqualTo(nint.Zero));
            });
        }
        finally
        {
            capture.Dispose();
        }
    }

    [Test]
    public async Task Capture_WhenRequestsOverlap_OwnsIndependentThreadsAndResults()
    {
        using var source = await CaptureSource.CreateAsync();
        var requests = Enumerable.Range(0, 3).Select(_ => CaptureAsync(source)).ToArray();
        var captures = new List<IVisualElementCapture>();
        try
        {
            foreach (var request in requests) captures.Add(await request);
            var threads = captures.Select(capture => GetField<Thread>(capture, "_thread")).ToArray();
            Assert.That(threads.Distinct().Count(), Is.EqualTo(captures.Count));
            captures[0].Dispose();
            Assert.Multiple(() =>
            {
                Assert.That(threads[0].IsAlive, Is.False);
                Assert.That(threads[1].IsAlive, Is.True);
                Assert.That(threads[2].IsAlive, Is.True);
                Assert.That(ReadPixels(captures[1]), Has.Length.GreaterThan(0));
                Assert.That(ReadPixels(captures[2]), Has.Length.GreaterThan(0));
            });
        }
        finally
        {
            // Recover every successful result even if another request failed.
            foreach (var request in requests)
            {
                try
                {
                    (await request).Dispose();
                }
                catch
                {
                }
            }
        }
    }

    [Test]
    public async Task Dispose_WhenOwnerThreadHasQueuedWork_WaitsForItBeforeReleasingPixels()
    {
        using var source = await CaptureSource.CreateAsync();
        var capture = await CaptureAsync(source);
        var queue = await GetField<TaskCompletionSource<DispatcherQueue?>>(capture, "_dispatcherReady").Task;
        if (queue is null) throw new InvalidOperationException("The capture dispatcher was not created.");
        using var hasEntered = new ManualResetEventSlim();
        using var canReturn = new ManualResetEventSlim();
        var hasQueued = queue.TryEnqueue(() =>
        {
            hasEntered.Set();
            canReturn.Wait(TimeSpan.FromSeconds(10));
        });
        Task? disposal = null;
        try
        {
            Assert.That(hasQueued, Is.True);
            Assert.That(hasEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var hasStartedDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = Task.Run(() =>
            {
                hasStartedDisposal.SetResult();
                capture.Dispose();
            });
            await hasStartedDisposal.Task;
            Assert.Multiple(() =>
            {
                Assert.That(disposal.IsCompleted, Is.False);
                Assert.That(capture.Data, Is.Not.EqualTo(nint.Zero));
            });
        }
        finally
        {
            canReturn.Set();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            capture.Dispose();
        }
        Assert.That(GetField<Thread>(capture, "_thread").IsAlive, Is.False);
    }

    [Test]
    public async Task Dispose_WhenCalledAgainDuringShutdown_WaitsForShutdownDeferral()
    {
        using var source = await CaptureSource.CreateAsync();
        var capture = await CaptureAsync(source);
        var queue = await GetField<TaskCompletionSource<DispatcherQueue?>>(capture, "_dispatcherReady").Task;
        if (queue is null) throw new InvalidOperationException("The capture dispatcher was not created.");
        var shutdownStarted = new TaskCompletionSource<Deferral>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownCount = 0;
        queue.ShutdownStarting += (_, args) =>
        {
            Interlocked.Increment(ref shutdownCount);
            shutdownStarted.SetResult(args.GetDeferral());
        };

        var disposal = Task.Run(capture.Dispose);
        var deferral = await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repeatedDisposal = Task.Run(capture.Dispose);
        try
        {
            Assert.ThrowsAsync<TimeoutException>(async () => await disposal.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.ThrowsAsync<TimeoutException>(async () => await repeatedDisposal.WaitAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            deferral.Complete();
            await Task.WhenAll(disposal, repeatedDisposal).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Multiple(() =>
        {
            Assert.That(shutdownCount, Is.EqualTo(1));
            Assert.That(GetField<Thread>(capture, "_thread").IsAlive, Is.False);
        });
    }

    [Test]
    public async Task Capture_WhenCanceledDuringStartup_CleansUpBeforeReturning()
    {
        using var source = await CaptureSource.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var request = Direct3D11ScreenCapture.CaptureAsync(source.Handle, default, new PixelRect(0, 0, 64, 48), cancellation.Token);
        cancellation.Cancel();
        try
        {
            using var capture = await request;
            Assert.Fail("Cancellation immediately after starting capture should win before delivery.");
        }
        catch (OperationCanceledException)
        {
            // CaptureAsync waits for its owner-thread teardown before propagating cancellation.
        }
        // A fresh capture verifies that canceling one session did not damage the source or another request.
        using var subsequent = await CaptureAsync(source);
        Assert.That(subsequent.Data, Is.Not.EqualTo(nint.Zero));
    }

    [Test]
    public async Task Capture_WhenInitializationFails_ExitsItsOwnerThread()
    {
        // Use the production constructor to retain the failed instance for thread-lifetime assertions.
        var constructor = typeof(Direct3D11ScreenCapture).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(nint), typeof(PixelPoint), typeof(PixelRect)], modifiers: null);
        if (constructor is null) throw new InvalidOperationException("The capture constructor was not found.");
        var capture = (Direct3D11ScreenCapture)constructor.Invoke([nint.Zero, default(PixelPoint), new PixelRect(0, 0, 64, 48)]);
        var completion = GetField<TaskCompletionSource>(capture, "_captureReady").Task;
        var failure = Assert.CatchAsync<Exception>(async () => await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        var disposals = Enumerable.Range(0, 2).Select(_ => Task.Run<Exception?>(() =>
        {
            try
            {
                capture.Dispose();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).ToArray();
        var disposalErrors = await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            // Every waiting caller observes the original failure only after owner-thread shutdown.
            foreach (var error in disposalErrors) Assert.That(error, Is.SameAs(failure));
            Assert.That(GetField<Thread>(capture, "_thread").IsAlive, Is.False);
        });
    }

    private static Task<IVisualElementCapture> CaptureAsync(CaptureSource source)
    {
        return Direct3D11ScreenCapture.CaptureAsync(source.Handle, default, new PixelRect(0, 0, 64, 48));
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = typeof(Direct3D11ScreenCapture).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException($"Missing capture field: {name}");
        return (T)field.GetValue(instance)!;
    }

    private static byte[] ReadPixels(IVisualElementCapture capture)
    {
        var data = new byte[checked(capture.Stride * capture.Size.Height)];
        Marshal.Copy(capture.Data, data, 0, data.Length);
        return data;
    }

    private sealed class CaptureSource(HWND handle) : IDisposable
    {
        public HWND Handle { get; } = handle;

        public static async Task<CaptureSource> CreateAsync()
        {
            var handle = await MessageWindow.Shared.InvokeAsync(CreateWindow);
            return new CaptureSource(handle);
        }

        private static unsafe HWND CreateWindow()
        {
            const string className = "STATIC";
            const string title = "Everywhere capture lifecycle test";
            fixed (char* windowClass = className)
            fixed (char* windowTitle = title)
            {
                var handle = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                    windowClass, windowTitle,
                    WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
                    0, 0, 160, 100,
                    hInstance: PInvoke.GetModuleHandle(default(PCWSTR)));
                if (handle.IsNull) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return handle;
            }
        }

        public void Dispose() => MessageWindow.Shared.Invoke(() => PInvoke.DestroyWindow(Handle));
    }
}
