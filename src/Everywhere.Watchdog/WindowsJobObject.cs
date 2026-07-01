#if WINDOWS

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Everywhere.Utilities;
using Microsoft.Win32.SafeHandles;

namespace Everywhere.Watchdog;

[SupportedOSPlatform("windows5.1.2600")]
internal sealed class WindowsJobObject : IDisposable
{
    private SafeFileHandle? _handle;
    private bool _disposed;

    public unsafe WindowsJobObject()
    {
        _handle = PInvoke.CreateJobObject();
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };
        var infoSpan = new Span<byte>(&info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        if (!PInvoke.SetInformationJobObject(_handle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, infoSpan))
        {
            var error = Marshal.GetLastPInvokeError();
            DisposeHelper.DisposeToDefault(ref _handle);
            throw new Win32Exception(error);
        }
    }

    public void AssignProcess(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WindowsJobObject));

        if (!PInvoke.AssignProcessToJobObject((HANDLE)(_handle?.DangerousGetHandle() ?? 0), (HANDLE)process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Terminate()
    {
        if (_disposed) return;
        PInvoke.TerminateJobObject(_handle, 1);
    }

    public unsafe void ClearKillOnJobClose()
    {
        if (_disposed) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0 // Clear the flag
            }
        };
        var infoSpan = new Span<byte>(&info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        PInvoke.SetInformationJobObject(_handle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, infoSpan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeHelper.DisposeToDefault(ref _handle);
    }
}

#endif