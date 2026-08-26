using Everywhere.ProcessIsolation.Watchdog;
#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
#else
using System.Diagnostics;
#endif

namespace Everywhere.Watchdog;

/// <summary>
/// Connection-owned registration table. Closing the sole authenticated Main
/// connection disposes this service and terminates every remaining registration.
/// </summary>
#if WINDOWS
[SupportedOSPlatform("windows6.0.6000")]
#endif
internal sealed class WatchdogRpcService : IWatchdogRpc, IDisposable
{
    private readonly Dictionary<ulong, MonitoredProcess> _registrations = [];
#if WINDOWS
    private readonly SafeProcessHandle _mainProcess;
#endif

    private ulong _nextHandle;

    private WatchdogRpcService(
#if WINDOWS
        SafeProcessHandle mainProcess
#endif
    )
    {
#if WINDOWS
        _mainProcess = mainProcess;
#endif
    }

#if WINDOWS
    /// <summary>Opens Main only for duplicating process handles into this process.</summary>
    public static WatchdogRpcService Create(uint mainProcessId)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE, false, mainProcessId);
        var mainProcess = new SafeProcessHandle(handle, ownsHandle: true);
        if (mainProcess.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            mainProcess.Dispose();
            throw new Win32Exception(error);
        }

        return new WatchdogRpcService(mainProcess);
    }
#else
    public static WatchdogRpcService Create() => new();
#endif

    /// <inheritdoc />
    public ValueTask<RegisterWatchdogProcessResponse> RegisterProcessAsync(
        RegisterWatchdogProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        var process = TryCaptureProcess(request);
        if (process is null)
        {
            return ValueTask.FromResult(
                new RegisterWatchdogProcessResponse
                {
                    Registered = false,
                    RegistrationHandle = 0
                });
        }

        var handle = ++_nextHandle;
        _registrations.Add(handle, process);
        Console.WriteLine($"Registered process {process.ProcessId} as lease {handle}.");
        return ValueTask.FromResult(
            new RegisterWatchdogProcessResponse
            {
                Registered = true,
                RegistrationHandle = handle
            });
    }

    /// <inheritdoc />
    public ValueTask<UnregisterWatchdogProcessResponse> UnregisterProcessAsync(
        UnregisterWatchdogProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_registrations.Remove(request.RegistrationHandle, out var process))
        {
            return ValueTask.FromResult(new UnregisterWatchdogProcessResponse { Found = false });
        }

        try
        {
            if (request.KillIfRunning)
            {
                process.Terminate();
            }
            else
            {
                process.ReleaseWithoutTermination();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Failed to release process lease {request.RegistrationHandle}: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }

        Console.WriteLine($"Released process lease {request.RegistrationHandle} (kill={request.KillIfRunning}).");
        return ValueTask.FromResult(new UnregisterWatchdogProcessResponse { Found = true });
    }

    /// <summary>Terminates all leases when Main's connection ends.</summary>
    public void Dispose()
    {
        foreach (var process in _registrations.Values)
        {
            try
            {
                process.Terminate();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Failed to terminate process {process.ProcessId}: {exception.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
        _registrations.Clear();
#if WINDOWS
        _mainProcess.Dispose();
#endif
    }

#if WINDOWS
    private MonitoredProcess? TryCaptureProcess(RegisterWatchdogProcessRequest request)
    {
        if (request.SourceProcessHandle == 0)
        {
            return null;
        }

        using var sourceProcessHandle = new SafeProcessHandle(
            (HANDLE)(nint)request.SourceProcessHandle,
            ownsHandle: false);
        using var currentProcessHandle = new SafeProcessHandle(
            PInvoke.GetCurrentProcess(),
            ownsHandle: false);
        if (!PInvoke.DuplicateHandle(
                _mainProcess,
                sourceProcessHandle,
                currentProcessHandle,
                out var duplicatedHandle,
                0,
                false,
                DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS))
        {
            Console.Error.WriteLine(
                $"Failed to duplicate Main's handle for process {request.ProcessId}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            return null;
        }

        return new WindowsMonitoredProcess(request.ProcessId, duplicatedHandle);
#else
    private static UnixMonitoredProcess? TryCaptureProcess(RegisterWatchdogProcessRequest request)
    {
        return UnixMonitoredProcess.TryCapture(request);
#endif
    }

    /// <summary>Platform resource owned by one opaque registration handle.</summary>
    private abstract class MonitoredProcess : IDisposable
    {
        public abstract int ProcessId { get; }

        public abstract void Terminate();

        public abstract void ReleaseWithoutTermination();

        public abstract void Dispose();
    }

#if WINDOWS
    /// <summary>Owns the single handle duplicated from Main and its kill-on-close Job Object.</summary>
    private sealed class WindowsMonitoredProcess : MonitoredProcess
    {
        public override int ProcessId { get; }

        private readonly SafeHandle _processHandle;
        private readonly WindowsJobObject? _jobObject;

        public WindowsMonitoredProcess(int processId, SafeHandle processHandle)
        {
            ProcessId = processId;
            _processHandle = processHandle;
            try
            {
                _jobObject = new WindowsJobObject();
                _jobObject.AssignProcess(processHandle);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Process {processId} could not be assigned to a Job Object: {exception.Message}");
                _jobObject?.Dispose();
                _jobObject = null;
            }
        }

        public override void Terminate()
        {
            if (_jobObject is not null)
            {
                _jobObject.Terminate();
                return;
            }

            // The fallback still targets the captured handle, never a recycled PID.
            PInvoke.TerminateProcess(_processHandle, 1);
        }

        public override void ReleaseWithoutTermination() => _jobObject?.ClearKillOnJobClose();

        public override void Dispose()
        {
            _jobObject?.Dispose();
            _processHandle.Dispose();
        }
    }
#else
    /// <summary>Unix fallback bound to PID plus process start time.</summary>
    private sealed class UnixMonitoredProcess(Process process, long startTimeUtcTicks) : MonitoredProcess
    {
        public override int ProcessId => process.Id;

        public static UnixMonitoredProcess? TryCapture(RegisterWatchdogProcessRequest request)
        {
            try
            {
                var process = Process.GetProcessById(request.ProcessId);
                var startTime = process.StartTime.ToUniversalTime().Ticks;
                if (startTime != request.ProcessStartTimeUtcTicks)
                {
                    process.Dispose();
                    return null;
                }

                return new UnixMonitoredProcess(process, startTime);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public override void Terminate()
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }

        public override void ReleaseWithoutTermination()
        {
        }

        public override void Dispose() => process.Dispose();
    }
#endif
}