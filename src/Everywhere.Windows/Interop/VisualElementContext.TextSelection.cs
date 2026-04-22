using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using Everywhere.Interop;
using Everywhere.Utilities;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

partial class VisualElementContext
{
    private readonly Subject<TextSelectionData> _textSelectionSubject = new();
    private IDisposable? _hookSubscription;
    private int _subscriberCount;

    public IDisposable Subscribe(IObserver<TextSelectionData> observer)
    {
        var subscription = _textSelectionSubject.Subscribe(observer);

        // Start monitoring when the first subscriber arrives
        if (Interlocked.Increment(ref _subscriberCount) == 1)
        {
            StartTextSelectionMonitoring();
        }

        return Disposable.Create(() =>
        {
            subscription.Dispose();
            // Stop monitoring when the last subscriber leaves
            if (Interlocked.Decrement(ref _subscriberCount) == 0)
            {
                StopTextSelectionMonitoring();
            }
        });
    }

    private void StartTextSelectionMonitoring()
    {
        if (_hookSubscription != null) return;

        var detector = new TextSelectionDetector();
        detector.SelectionDetected += OnSelectionDetected;
        _hookSubscription = detector;
    }

    private void StopTextSelectionMonitoring()
    {
        if (_hookSubscription is TextSelectionDetector detector)
        {
            detector.SelectionDetected -= OnSelectionDetected;
            detector.Dispose();
        }
        _hookSubscription = null;
    }

    private void OnSelectionDetected(TextSelectionData data)
    {
        _textSelectionSubject.OnNext(data);
    }

    /// <summary>
    /// Detects text selection using mouse hooks.
    /// Ported from selection-hook
    /// https://github.com/0xfullex/selection-hook
    /// </summary>
    private sealed class TextSelectionDetector : IDisposable
    {
        public event Action<TextSelectionData>? SelectionDetected;

        private readonly IDisposable _mouseHookSubscription;
        private readonly ReusableCancellationTokenSource _reusableCancellationTokenSource = new();

        private bool _isMouseDown;
        private Point _mouseDownPos;
        private long _mouseDownTime;
        private HWND _mouseDownHwnd;
        private RECT _mouseDownRect;

        private Point _lastMouseUpPos;
        private long _lastMouseUpTime;

        // System state cache
        private bool _lastShouldProcessResult = true;
        private long _lastShouldProcessCheckTime;

        // Clipboard fallback support
        private HCURSOR _mouseDownCursor;
        private HCURSOR _mouseUpCursor;

        // Atomic flags
        private volatile int _isProcessing; // 0 = false, 1 = true

        // Constants from selection-hook.cc
        private const int MIN_DRAG_DISTANCE = 8;
        private const int MAX_DRAG_TIME_MS = 8000;
        private const int DOUBLE_CLICK_MAX_DISTANCE = 3;
        private const int DOUBLE_CLICK_TIME_MS = 500;
        private const int SYSTEM_STATE_CACHE_MS = 10000;

        // Win32 constants
        private const uint CF_DIB = 8;
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_HDROP = 15;

        /// <summary>
        /// Process names to exclude from clipboard fallback strategy.
        /// </summary>
        private static readonly HashSet<string> ExcludeProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Screenshot
            "snipaste.exe",
            "pixpin.exe",
            "sharex.exe",
            // Office
            "excel.exe",
            "powerpnt.exe",
            // Image Editor
            "photoshop.exe",
            "illustrator.exe",
            // Video Editor
            "adobe premiere pro.exe",
            "afterfx.exe",
            // Audio Editor
            "adobe audition.exe",
            // 3D Editor
            "blender.exe",
            "3dsmax.exe",
            "maya.exe",
            // CAD
            "acad.exe",
            "sldworks.exe",
            // Remote Desktop
            "mstsc.exe"
        };

        /// <summary>
        /// Process names to exclude from cursor detection (always allow clipboard fallback).
        /// </summary>
        /// from: https://github.com/CherryHQ/cherry-studio/blob/c7c380d706667f2f252438c971adade603f894e3/src/main/configs/SelectionConfig.ts
        private static readonly HashSet<string> CursorDetectExcludeProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "acrobat.exe", "wps.exe", "cajviewer.exe"
        };

        /// <summary>
        /// Process names that require delay reading from clipboard after copy command.
        /// </summary>
        /// from: https://github.com/CherryHQ/cherry-studio/blob/c7c380d706667f2f252438c971adade603f894e3/src/main/configs/SelectionConfig.ts
        private static readonly HashSet<string> ClipboardDelayReadProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "acrobat.exe", "wps.exe", "cajviewer.exe", "foxitphantom.exe"
        };

        /// <summary>
        /// Process names that should not use Ctrl + C for clipboard fallback due to potential interference with user copy or app behavior.
        /// </summary>
        private static readonly HashSet<string> NoCtrlCProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe", "powershell.exe", "WindowsTerminal.exe", "wt.exe", "conhost.exe"
        };

        private enum CopyKeyType
        {
            CtrlInsert,
            CtrlC
        }

        public TextSelectionDetector()
        {
            _mouseHookSubscription = LowLevelHook.CreateMouseHook(MouseHookCallback);
        }

        private void MouseHookCallback(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            switch (msg)
            {
                case WINDOW_MESSAGE.WM_LBUTTONDOWN:
                {
                    _isMouseDown = true;
                    _mouseDownPos = hookStruct.pt;
                    _mouseDownTime = Environment.TickCount64;
                    CaptureCursor(ref _mouseDownCursor);

                    // Capture window state for HasWindowMoved check
                    _mouseDownHwnd = PInvoke.WindowFromPoint(_mouseDownPos);
                    if (_mouseDownHwnd != HWND.Null)
                    {
                        PInvoke.GetWindowRect(_mouseDownHwnd, out _mouseDownRect);
                    }
                    else
                    {
                        _mouseDownRect = default;
                    }
                    break;
                }
                case WINDOW_MESSAGE.WM_LBUTTONUP:
                {
                    CaptureCursor(ref _mouseUpCursor);
                    if (_isMouseDown)
                    {
                        _isMouseDown = false;
                        var mouseUpPos = hookStruct.pt;
                        var mouseUpTime = Environment.TickCount64;

                        ProcessMouseUp(mouseUpPos, mouseUpTime);

                        _lastMouseUpPos = mouseUpPos;
                        _lastMouseUpTime = mouseUpTime;
                    }
                    break;
                }
            }
        }

        private static void CaptureCursor(ref HCURSOR cursorStore)
        {
            var ci = new CURSORINFO { cbSize = (uint)Unsafe.SizeOf<CURSORINFO>() };
            if (PInvoke.GetCursorInfo(ref ci))
            {
                cursorStore = ci.hCursor;
            }
        }

        private void ProcessMouseUp(Point mouseUpPos, long mouseUpTime)
        {
            _reusableCancellationTokenSource.Cancel();

            if (!ShouldProcessGetSelection()) return;

            var shouldDetectSelection = false;

            // 1. Drag Detection
            var dx = mouseUpPos.X - _mouseDownPos.X;
            var dy = mouseUpPos.Y - _mouseDownPos.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var duration = mouseUpTime - _mouseDownTime;

            var isDrag = distance >= MIN_DRAG_DISTANCE && duration <= MAX_DRAG_TIME_MS;

            // Check window movement
            var currentHwnd = PInvoke.WindowFromPoint(mouseUpPos);
            var windowStable = false;
            if (currentHwnd == _mouseDownHwnd && currentHwnd != HWND.Null)
            {
                PInvoke.GetWindowRect(currentHwnd, out var currentRect);
                windowStable = !HasWindowMoved(_mouseDownRect, currentRect);
            }

            if (isDrag && windowStable)
            {
                // Console.WriteLine("Should Detect Selection via [Drag]");
                shouldDetectSelection = true;
            }

            // 2. Double Click Detection
            if (!shouldDetectSelection)
            {
                var dcDx = mouseUpPos.X - _lastMouseUpPos.X;
                var dcDy = mouseUpPos.Y - _lastMouseUpPos.Y;
                var dcDistance = Math.Sqrt(dcDx * dcDx + dcDy * dcDy);
                var timeSinceLastClick = mouseUpTime - _lastMouseUpTime;

                if (timeSinceLastClick <= DOUBLE_CLICK_TIME_MS && dcDistance <= DOUBLE_CLICK_MAX_DISTANCE)
                {
                    // Check window stability for double click too (as per reference.cc)
                    if (windowStable)
                    {
                        // Console.WriteLine("Should Detect Selection via [Double Click]");
                        shouldDetectSelection = true;
                    }
                }
            }

            // 3. Shift + Click Detection
            if (!shouldDetectSelection)
            {
                var isShiftPressing = IsKeyPressing(VIRTUAL_KEY.VK_SHIFT);
                var isCtrlPressing = IsKeyPressing(VIRTUAL_KEY.VK_CONTROL);
                var isAltPressing = IsKeyPressing(VIRTUAL_KEY.VK_MENU);

                if (isShiftPressing && !isCtrlPressing && !isAltPressing)
                {
                    // Console.WriteLine("Should Detect Selection via [Shift Click]");
                    shouldDetectSelection = true;
                }
            }

            if (shouldDetectSelection)
            {
                // Debounce the detection to handle multi-click scenarios (double/triple click)
                // This prevents multiple detection triggers in rapid succession.
                var cancellationToken = _reusableCancellationTokenSource.Token;
                Task.Run(() => BeginDetectAsync(currentHwnd, cancellationToken), cancellationToken);
            }
        }

        private bool ShouldProcessGetSelection()
        {
            var now = Environment.TickCount64;
            if (now - _lastShouldProcessCheckTime < SYSTEM_STATE_CACHE_MS)
            {
                return _lastShouldProcessResult;
            }

            _lastShouldProcessCheckTime = now;

            if (PInvoke.SHQueryUserNotificationState(out var state).Succeeded)
            {
                if (state is
                    QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or
                    QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                {
                    _lastShouldProcessResult = false;
                    return false;
                }
            }

            _lastShouldProcessResult = true;
            return true;
        }

        /// <summary>
        /// Check if window has moved by comparing RECTs. Given a 2 pixel offset tolerance.
        /// </summary>
        /// <param name="r1"></param>
        /// <param name="r2"></param>
        /// <returns></returns>
        private static bool HasWindowMoved(RECT r1, RECT r2) =>
            Math.Abs(r1.left - r2.left) > 2 ||
            Math.Abs(r1.top - r2.top) > 2 ||
            Math.Abs(r1.right - r2.right) > 2 ||
            Math.Abs(r1.bottom - r2.bottom) > 2;

        /// <summary>
        /// Begin the detection process with a debounce to handle multi-click scenarios.
        /// This prevents multiple detection triggers in rapid succession.
        /// </summary>
        /// <param name="hWnd"></param>
        /// <param name="cancellationToken"></param>
        private async Task BeginDetectAsync(HWND hWnd, CancellationToken cancellationToken)
        {
            try
            {
                // Debounce delay: Wait for multi-click sequence to settle.
                // If user double clicks, we likely don't want to trigger immediately if they are about to triple click.
                await Task.Delay(TimeSpan.FromMilliseconds(DOUBLE_CLICK_TIME_MS), cancellationToken);

                // If cancellation requested, it means another click happened, we should abort this detection.
                if (cancellationToken.IsCancellationRequested) return;

                await DetectAsync(hWnd, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when a new click happens before debounce delay, just ignore.
            }
            catch (ObjectDisposedException)
            {
                // This can happen if the detector is disposed while a detection task is still running. Just ignore.
            }
            catch (Exception ex)
            {
                Log.ForContext<TextSelectionDetector>().Error(ex, "Error in BeginDetectAsync");
            }
        }

        private async Task DetectAsync(HWND hWnd, CancellationToken cancellationToken)
        {
            // Check processing flag
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 1) return;

            try
            {
                var processName = GetProcessInformationByHwnd(hWnd, out var pid) ?? string.Empty;
                if (cancellationToken.IsCancellationRequested) return;

                if (ExcludeProcessNames.Contains(processName))
                {
                    return;
                }

                string? text = null;
                IVisualElement? visualElement = null;
                var uiaControlType = ControlType.Unknown;

                // 1. Try to get selection from element
                // Console.WriteLine("1. TryGetSelectionTextFromElement");
                TryGetSelectionTextFromElement();
                if (cancellationToken.IsCancellationRequested) return;

                // 2. Fallback to Clipboard
                if (string.IsNullOrEmpty(text) && ShouldProcessViaClipboard(uiaControlType, processName))
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    // Console.WriteLine("2. GetTextViaClipboardAsync");
                    text = await GetTextViaClipboardAsync(processName, cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested) return;

                // Trigger event whatever we got
                // A null or empty text indicates selection was canceled or failed
                SelectionDetected?.Invoke(new TextSelectionData(text, visualElement));

                void TryGetSelectionTextFromElement()
                {
                    AutomationElement? element;
                    try
                    {
                        element = Automation.FocusedElement();
                        if (element is null) return;
                        if (element.Properties.ProcessId.ValueOrDefault != pid) return;
                    }
                    catch
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested) return;

                    element.Properties.ControlType.TryGetValue(out uiaControlType);
                    visualElement = new AutomationVisualElementImpl(element);
                    text = visualElement.GetSelectionText();
                }
            }
            catch (Exception ex)
            {
                var isExpected = ex is COMException or InvalidOperationException or TimeoutException;

                // Ignore errors during detection
                if (!isExpected) Log.ForContext<TextSelectionDetector>().Warning(ex, "Error during text selection detection");
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        private static string? GetProcessInformationByHwnd(HWND hWnd, out uint pid)
        {
            pid = 0;
            if (hWnd == HWND.Null) return null;

            PInvoke.GetWindowThreadProcessId(hWnd, out pid);
            if (pid == 0) return null;

            var hProcess = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            using var safeProcessHandle = new SafeProcessHandle(hProcess, true);

            Span<char> buffer = stackalloc char[260];
            var size = (uint)buffer.Length;
            var result = PInvoke.QueryFullProcessImageName(safeProcessHandle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size);

            if (!result || size == 0) return null;

            var fullPath = new string(buffer[..(int)size]);
            return Path.GetFileName(fullPath);
        }

        /// <summary>
        /// Check if we should process GetTextViaClipboard
        /// </summary>
        /// <returns></returns>
        private bool ShouldProcessViaClipboard(ControlType uiaControlType, string processName)
        {
            // when mouse down or up, any one of them is beamCursor, we can use clipboard
            // otherwise, we have to check the situation further

            // Load common cursors every time to avoid caching issues
            var cursorIBeam = PInvoke.LoadCursor(default, PInvoke.IDC_IBEAM);
            var cursorArrow = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW);
            var cursorHand = PInvoke.LoadCursor(default, PInvoke.IDC_HAND);

            // beam cursor detected: valid text selection
            if (_mouseDownCursor == cursorIBeam || _mouseUpCursor == cursorIBeam) return true;

            // not beam, not arrow, not hand: invalid text selection cursor
            if (_mouseUpCursor != cursorArrow && _mouseUpCursor != cursorHand)
            {
                // only apps in the list can use clipboard (exclude cursor detection)
                return CursorDetectExcludeProcessNames.Contains(processName);
            }

            //
            // not beam, but arrow or hand:
            //
            // uiaControlType exceptions (when the cursor is arrow or hand):
            //
            // https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controltype-ids
            //
            // chrome devtools: UIA_GroupControlTypeId (50026)
            // chrome pages: UIA_DocumentControlTypeId (50030), UIA_TextControlTypeId (50020)
            //
            return uiaControlType is ControlType.Unknown or ControlType.Group or ControlType.Document or ControlType.Text;
        }

        private async static Task<string?> GetTextViaClipboardAsync(string processName, CancellationToken cancellationToken)
        {
            // 1. User Intent Check (Avoid interfering with user copy)
            if (ShouldAbortForUserIntent(out var text))
            {
                // If user copied text successfully, set it and return true
                // If no text, return false to skip clipboard strategy
                // Console.WriteLine("AbortForUserIntent");
                return text;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Backup Clipboard
            // We backup Text or Image (CF_DIB) to match standard behavior (like arboard)
            // This prevents losing screenshots when a text selection fallback occurs.
            byte[]? backupData = null;
            uint backupFormat = 0;

            if (PInvoke.OpenClipboard())
            {
                // Priority 1: Text
                if (TryGetClipboardData(CF_UNICODETEXT, out backupData))
                {
                    // Console.WriteLine("Priority 1: Text");
                    backupFormat = CF_UNICODETEXT;
                }
                // Priority 2: Image (DIB)
                else if (TryGetClipboardData(CF_DIB, out backupData))
                {
                    // Console.WriteLine("Priority 2: Image (DIB)");
                    backupFormat = CF_DIB;
                }
                // Priority 3: Files (CF_HDROP)
                // This preserves file selection (e.g. copied files in Explorer) which is a common scenario users don't want to lose.
                // It is represented as a list of file paths.
                else if (TryGetClipboardData(CF_HDROP, out backupData))
                {
                    // Console.WriteLine("Priority 3: Files (CF_HDROP)");
                    backupFormat = CF_HDROP;
                }

                // Note: We don't empty here, as that might clear file handles or other formats we didn't backup.
                // We rely on the Copy command to overwrite ownership.
                PInvoke.CloseClipboard();
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var isInDelayReadList = ClipboardDelayReadProcessNames.Contains(processName);

                // 3. Strategy A: Ctrl + Insert (Safer, rarely overridden)
                if (!isInDelayReadList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShouldKeyInterruptViaClipboard())
                    {
                        // Console.WriteLine("Strategy A: ShouldKeyInterruptViaClipboard");
                        return null;
                    }

                    var clipboardSequence = PInvoke.GetClipboardSequenceNumber();
                    SendCopyKey(CopyKeyType.CtrlInsert);

                    var hasClipboardChanged = false;
                    // max wait time about 5m * 20 = 100ms
                    for (var i = 0; i < 20; i++)
                    {
                        if (PInvoke.GetClipboardSequenceNumber() != clipboardSequence)
                        {
                            // Console.WriteLine($"Strategy A: hasClipboardChanged at {i}");
                            hasClipboardChanged = true;
                            break;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
                    }

                    // Handle case when clipboard update was detected
                    cancellationToken.ThrowIfCancellationRequested();
                    if (hasClipboardChanged)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);

                        if (TryGetClipboardText(out text, emptyClipboard: false))
                        {
                            // Console.WriteLine($"Strategy A: TryGetClipboardText: {text}");
                            return text;
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldKeyInterruptViaClipboard())
                {
                    // Console.WriteLine("Strategy A-B: ShouldKeyInterruptViaClipboard");
                    return null;
                }

                // 4. Strategy B: Ctrl + C (Fallback)
                if (!NoCtrlCProcessNames.Contains(processName))
                {
                    var clipboardSequence = PInvoke.GetClipboardSequenceNumber();
                    SendCopyKey(CopyKeyType.CtrlC);

                    var hasClipboardChanged = false;
                    // max wait time about 5m * 36 = 180ms
                    for (var i = 0; i < 36; i++)
                    {
                        if (PInvoke.GetClipboardSequenceNumber() != clipboardSequence)
                        {
                            // Console.WriteLine($"Strategy B: hasClipboardChanged at {i}");
                            hasClipboardChanged = true;
                            break;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
                    }

                    // Handle case when clipboard update was detected
                    if (!hasClipboardChanged)
                    {
                        return null;
                    }
                }

                // some apps will change the clipboard content many times after the first time GetClipboardSequenceNumber() changed
                // so we need to wait a little bit (eg. Adobe Acrobat) for those app in the delay read list
                cancellationToken.ThrowIfCancellationRequested();
                if (isInDelayReadList)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(135), cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);

                if (ShouldKeyInterruptViaClipboard())
                {
                    return null;
                }

                // Final Attempt
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetClipboardText(out text, emptyClipboard: false))
                {
                    // Console.WriteLine($"Strategy A: TryGetClipboardText: {text}");
                    return text;
                }
            }
            finally
            {
                // 5. Restore Clipboard
                if (backupFormat != 0 && backupData != null)
                {
                    SetClipboardData(backupFormat, backupData);
                }
                else
                {
                    // If we didn't have recognized data, clear the clipboard to remove the "Selected Text"
                    if (PInvoke.OpenClipboard())
                    {
                        PInvoke.EmptyClipboard();
                        PInvoke.CloseClipboard();
                    }
                }
            }

            return text;
        }

        /// <summary>
        /// Check if user is trying to copy text manually, to avoid interfering.
        /// </summary>
        /// <param name="userCopiedText"></param>
        /// <returns>true to abort the clipboard strategy</returns>
        private static bool ShouldAbortForUserIntent(out string? userCopiedText)
        {
            userCopiedText = null;

            var isCtrlPressed = false;
            var isCPressed = false;
            var isXPressed = false;
            var isVPressed = false;

            // Check keys: Ctrl, C, X, V
            // If none pressing, return false (do not abort)
            // If pressing, monitor for clipboard change or timeout

            var initSeq = PInvoke.GetClipboardSequenceNumber();
            int checkCount;
            const int MaxChecks = 5;
            for (checkCount = 0; checkCount < MaxChecks; checkCount++)
            {
                // Check if clipboard sequence number has changed since mouse down
                // if it's changed, it means user has copied something, we can read it directly
                if (PInvoke.GetClipboardSequenceNumber() != initSeq)
                {
                    // User copied something! Try to read from clipboard directly
                    return TryGetClipboardText(out userCopiedText) && !string.IsNullOrEmpty(userCopiedText);
                }


                var isCtrlPressing = IsKeyPressing(VIRTUAL_KEY.VK_CONTROL);
                var isCPressing = IsKeyPressing(VIRTUAL_KEY.VK_C);
                var isXPressing = IsKeyPressing(VIRTUAL_KEY.VK_X);
                var isVPressing = IsKeyPressing(VIRTUAL_KEY.VK_V);

                // if no key is pressing, we can break to go on
                if (!isCtrlPressing && !isCPressing && !isXPressing && !isVPressing)
                {
                    break;
                }

                isCtrlPressed |= isCtrlPressing;
                isCPressed |= isCPressing;
                isXPressed |= isXPressing;
                isVPressed |= isVPressing;

                Thread.Sleep(40);
            }

            // wait for user copy timeout, still some key(Ctrl, C, X, V) is pressing
            if (checkCount >= MaxChecks)
            {
                return true;
            }

            // if it's a user copy behavior, we will do nothing
            if (isCtrlPressed && (isCPressed || isXPressed || isVPressed))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Send copy key combination based on type
        /// </summary>
        /// <param name="type"></param>
        private static void SendCopyKey(CopyKeyType type)
        {
            var isCtrlPressing = IsKeyPressing(VIRTUAL_KEY.VK_CONTROL);
            var isCPressing = IsKeyPressing(VIRTUAL_KEY.VK_C);
            var isInsertPressing = IsKeyPressing(VIRTUAL_KEY.VK_INSERT);

            if (isCtrlPressing && (isCPressing || isInsertPressing))
            {
                // User is already pressing the copy key, skip sending
                return;
            }

            // Check modifiers to release
            var inputs = new List<INPUT>(6);

            // if Alt is pressing, we need to release it first
            var isAltPressing = IsKeyPressing(VIRTUAL_KEY.VK_MENU);
            if (isAltPressing) AddKeyInput(inputs, VIRTUAL_KEY.VK_MENU, true);

            // Release Shift
            var isShiftPressing = IsKeyPressing(VIRTUAL_KEY.VK_SHIFT);
            if (isShiftPressing) AddKeyInput(inputs, VIRTUAL_KEY.VK_SHIFT, true);

            // The following Ctrl+Insert or Ctrl+C key combinations are symmetric, meaning press and release events come in pairs

            // Press Ctrl if not pressing
            if (!isCtrlPressing) AddKeyInput(inputs, VIRTUAL_KEY.VK_CONTROL, false);

            // Press Key (C or Insert)
            var key = type == CopyKeyType.CtrlInsert ? VIRTUAL_KEY.VK_INSERT : VIRTUAL_KEY.VK_C;
            AddKeyInput(inputs, key, false);
            // Release Key
            AddKeyInput(inputs, key, true);

            // Release Ctrl if we pressing it
            if (!isCtrlPressing) AddKeyInput(inputs, VIRTUAL_KEY.VK_CONTROL, true);

            // Note: We don't restore Alt/Shift state as that might be complex/unwanted
            if (inputs.Count > 0)
            {
                unsafe
                {
                    var inputArr = inputs.ToArray();
                    fixed (INPUT* pInputs = inputArr)
                    {
                        PInvoke.SendInput((uint)inputArr.Length, pInputs, Unsafe.SizeOf<INPUT>());
                        // Console.WriteLine($"SendCopyKey: {type}");
                    }
                }
            }
        }

        /// <summary>
        /// Check if some key is interrupted the copy process via clipboard
        /// </summary>
        /// <returns></returns>
        private static bool ShouldKeyInterruptViaClipboard()
        {
            // If Ctrl is pressing, assume user interference
            return IsKeyPressing(VIRTUAL_KEY.VK_CONTROL);
        }

        private static bool IsKeyPressing(VIRTUAL_KEY vk)
        {
            return (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;
        }

        private static void AddKeyInput(List<INPUT> inputs, VIRTUAL_KEY vk, bool keyUp)
        {
            var input = new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous = new INPUT._Anonymous_e__Union
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                    }
                }
            };
            inputs.Add(input);
        }

        /// <summary>
        /// Try to get text from clipboard. can optionally empty clipboard after reading.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="emptyClipboard"></param>
        /// <param name="openClipboard"></param>
        /// <returns></returns>
        private static unsafe bool TryGetClipboardText(out string? text, bool emptyClipboard = false, bool openClipboard = true)
        {
            text = null;
            if (openClipboard && !PInvoke.OpenClipboard()) return false;

            try
            {
                var handle = PInvoke.GetClipboardData(CF_UNICODETEXT);
                if (handle.Value == null) return false;

                var ptr = PInvoke.GlobalLock((HGLOBAL)handle.Value);
                if (ptr != null)
                {
                    text = Marshal.PtrToStringUni((nint)ptr);
                    PInvoke.GlobalUnlock((HGLOBAL)handle.Value);

                    if (emptyClipboard) PInvoke.EmptyClipboard();

                    return true;
                }
            }
            finally
            {
                if (openClipboard) PInvoke.CloseClipboard();
            }

            return false;
        }

        private static unsafe bool TryGetClipboardData(uint format, [NotNullWhen(true)] out byte[]? data)
        {
            data = null;
            // Assumes caller has opened clipboard!

            var handle = PInvoke.GetClipboardData(format);
            if (handle.Value == null) return false;

            var ptr = PInvoke.GlobalLock((HGLOBAL)handle.Value);
            if (ptr == null) return false;

            var size = (int)PInvoke.GlobalSize((HGLOBAL)handle.Value);
            if (size > 0)
            {
                data = new byte[size];
                Marshal.Copy((nint)ptr, data, 0, size);
            }
            PInvoke.GlobalUnlock((HGLOBAL)handle.Value);
            return data != null;
        }

        // Clipboard Format IDs for exclusion
        private static uint _cfHistory, _cfCloud;

        private static unsafe void SetClipboardExclusion(ref uint cfFormat, string format)
        {
            if (cfFormat == 0) cfFormat = PInvoke.RegisterClipboardFormat(format);

            // Allocation for DWORD 0. We don't need to release it.
            var hMem = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE | GLOBAL_ALLOC_FLAGS.GMEM_ZEROINIT, sizeof(int));
            PInvoke.SetClipboardData(cfFormat, (HANDLE)hMem.Value);
        }

        private static unsafe void SetClipboardData(uint format, byte[] data)
        {
            if (!PInvoke.OpenClipboard()) return;

            try
            {
                PInvoke.EmptyClipboard();

                var bytes = data.Length;
                var hGlobal = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes);
                if (hGlobal == 0) return;

                var targetPtr = PInvoke.GlobalLock(hGlobal);
                if (targetPtr == null) return;

                Marshal.Copy(data, 0, (nint)targetPtr, bytes);
                PInvoke.GlobalUnlock(hGlobal);

                // Set primary data
                PInvoke.SetClipboardData(format, (HANDLE)hGlobal.Value);

                // Apply Exclusions
                SetClipboardExclusion(ref _cfHistory, "CanIncludeInClipboardHistory");
                SetClipboardExclusion(ref _cfCloud, "CanUploadToCloudClipboard");
            }
            finally
            {
                PInvoke.CloseClipboard();
            }
        }

        public void Dispose()
        {
            _mouseHookSubscription.Dispose();
        }
    }
}