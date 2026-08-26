using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Everywhere.Utilities;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Callback for LowLevelHook. Return true to block the message.
/// </summary>
internal delegate void LowLevelHookHandler<T>(WINDOW_MESSAGE msg, ref T hookStruct, ref bool blockNext) where T : unmanaged;

/// <summary>
/// Manages low-level Windows hooks (Keyboard/Mouse) on a dedicated background thread to avoid blocking the UI thread.
/// </summary>
internal static class LowLevelHook
{
    public static IDisposable CreateMouseHook(LowLevelHookHandler<MSLLHOOKSTRUCT> callback, bool runOnDedicatedThread = true)
    {
        return new HookRunner<MSLLHOOKSTRUCT>(WINDOWS_HOOK_ID.WH_MOUSE_LL, callback, runOnDedicatedThread);
    }

    public static IDisposable CreateKeyboardHook(LowLevelHookHandler<KBDLLHOOKSTRUCT> callback, bool runOnDedicatedThread = true)
    {
        return new HookRunner<KBDLLHOOKSTRUCT>(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, callback, runOnDedicatedThread);
    }

    /// <summary>
    /// The actual generic implementation of the hook.
    /// </summary>
    private sealed class HookRunner<T> : IDisposable where T : unmanaged
    {
        private static TimeSpan StopTimeout => TimeSpan.FromSeconds(2);

        private AtomicBoolean IsDisposed => new(ref _isDisposed);

        private readonly LowLevelHookHandler<T> _callback;
        private readonly WINDOWS_HOOK_ID _id;
        private readonly ManualResetEventSlim _started = new(false);
        private readonly Thread? _thread;

        private int _isDisposed;
        private UnhookWindowsHookExSafeHandle? _hookHandle;
        private GCHandle _hookProcHandle;
        private Exception? _startupException;
        private uint _threadId;

        public HookRunner(WINDOWS_HOOK_ID id, LowLevelHookHandler<T> callback, bool runOnDedicatedThread)
        {
            _id = id;
            _callback = callback;

            if (!runOnDedicatedThread)
            {
                Install();
                _started.Set();
                return;
            }

            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "LowLevelHookThread",
                Priority = ThreadPriority.Highest
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.Wait();

            if (_startupException is not null)
            {
                throw new InvalidOperationException("Failed to start the low-level input hook thread.", _startupException);
            }
        }

        public void Dispose()
        {
            if (!IsDisposed.FlipIfFalse())
            {
                return;
            }

            if (_thread is null)
            {
                Uninstall();
                GC.SuppressFinalize(this);
                return;
            }

            var success = PInvoke.PostThreadMessage(_threadId, (uint)WINDOW_MESSAGE.WM_QUIT, 0, 0);
            if (!success)
            {
                Console.Error.WriteLine(
                    $"Failed to post a shutdown message to the low-level input hook thread. Error: {Marshal.GetLastWin32Error()}.");
            }

            if (Thread.CurrentThread != _thread && !_thread.Join(StopTimeout))
            {
                throw new TimeoutException("The low-level input hook thread did not stop within the cleanup deadline.");
            }

            GC.SuppressFinalize(this);
        }

        private unsafe void ThreadProc()
        {
            try
            {
                _threadId = PInvoke.GetCurrentThreadId();

                // PostThreadMessage requires the target thread to own a message queue.
                // Create it before publishing startup completion so Dispose can always
                // wake this thread and wait for native hook removal.
                MSG message;
                PInvoke.PeekMessage(&message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
                Install();
            }
            catch (Exception exception)
            {
                _startupException = exception;
                return;
            }
            finally
            {
                _started.Set();
            }

            try
            {
                while (true)
                {
                    var result = PInvoke.GetMessage(out var message, HWND.Null, 0, 0);
                    if (result <= 0 || result == (uint)WINDOW_MESSAGE.WM_QUIT)
                    {
                        break;
                    }

                    PInvoke.TranslateMessage(message);
                    PInvoke.DispatchMessage(message);
                }
            }
            finally
            {
                Uninstall();
            }
        }

        private void Install()
        {
            if (IsDisposed)
            {
                return;
            }

            using var hModule = PInvoke.GetModuleHandle();
            var hookProc = new HOOKPROC(HookProc);
            _hookProcHandle = GCHandle.Alloc(hookProc);
            _hookHandle = PInvoke.SetWindowsHookEx(_id, hookProc, hModule, 0);

            if (!_hookHandle.IsInvalid)
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();
            _hookHandle.Dispose();
            _hookHandle = null;
            _hookProcHandle.Free();
            throw new Win32Exception(error, "SetWindowsHookEx failed.");
        }

        private unsafe LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
        {
            if (code < 0)
            {
                return PInvoke.CallNextHookEx(null, code, wParam, lParam);
            }

            ref var hookStruct = ref Unsafe.AsRef<T>(lParam.Value.ToPointer());
            var blockNext = false;
            _callback.Invoke((WINDOW_MESSAGE)wParam.Value, ref hookStruct, ref blockNext);
            return blockNext ? (LRESULT)1 : PInvoke.CallNextHookEx(null, code, wParam, lParam);
        }

        private void Uninstall()
        {
            DisposeHelper.DisposeToDefault(ref _hookHandle);
            if (_hookProcHandle.IsAllocated)
            {
                _hookProcHandle.Free();
            }
        }

        ~HookRunner()
        {
            try
            {
                Dispose();
            }
            catch
            {
                // Ignore
            }
        }
    }
}