using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Everywhere.Automation;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Utilities;
using Everywhere.Windows.Automation;
using Everywhere.Windows.Interop.UIAutomation;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Monitors text selection through Windows accessibility, input, and clipboard facilities.
/// </summary>
public sealed class WindowsTextSelectionMonitorFactory : ITextSelectionMonitorFactory
{
    /// <inheritdoc />
    public ITextSelectionMonitor Create(
        ITextSelectionMonitorContext context,
        int mainProcessId,
        TextSelectionMonitoringConfiguration configuration,
        Func<TextSelectionObservation, CancellationToken, ValueTask> publish) =>
        new TextSelectionDetector(context, mainProcessId, configuration, publish);

    /// <summary>
    /// Detects text selection using mouse hooks.
    /// Ported from selection-hook
    /// https://github.com/0xfullex/selection-hook
    /// </summary>
    private sealed class TextSelectionDetector : ITextSelectionMonitor
    {
        private readonly ITextSelectionMonitorContext _context;
        private readonly Task _detectionWorker;
        private readonly Channel<DetectionTrigger> _pendingDetections = Channel.CreateBounded<DetectionTrigger>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private readonly Func<TextSelectionObservation, CancellationToken, ValueTask> _publish;
        private readonly IDisposable _mouseHookSubscription;
        private readonly ReusableCancellationTokenSource _reusableCancellationTokenSource = new();

        private TextSelectionMonitoringConfiguration _configuration;
        private int _isDisposed;
        private readonly uint _mainProcessId;

        private bool _isMouseDown;
        private Point _mouseDownPos;
        private long _mouseDownTime;
        private uint _mouseDownClipboardSequence;
        private HWND _mouseDownHwnd;
        private RECT _mouseDownRect;

        private Point _lastMouseUpPos;
        private long _lastMouseUpTime;

        // Clipboard fallback support
        private HCURSOR _mouseDownCursor;
        private HCURSOR _mouseUpCursor;

        // Constants from selection-hook.cc
        private const int MIN_DRAG_DISTANCE = 8;
        private const int MAX_DRAG_TIME_MS = 8000;
        private const int DOUBLE_CLICK_MAX_DISTANCE = 3;
        private const int DOUBLE_CLICK_TIME_MS = 500;

        // Win32 constants
        private const uint CF_DIB = 8;
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_HDROP = 15;

        /// <summary>
        /// Process names to exclude from clipboard fallback strategy.
        /// </summary>
        private static readonly HashSet<string> ClipboardFallbackExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
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
            "mstsc.exe",
            // Terminals: Ctrl+C may interrupt a foreground program and Ctrl+Insert is not universal.
            "cmd.exe",
            "conhost.exe",
            "powershell.exe",
            "pwsh.exe",
            "WindowsTerminal.exe",
            "wt.exe"
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
            "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe", "wt.exe", "conhost.exe"
        };

        private enum CopyKeyType
        {
            CtrlInsert,
            CtrlC
        }

        public TextSelectionDetector(
            ITextSelectionMonitorContext context,
            int mainProcessId,
            TextSelectionMonitoringConfiguration configuration,
            Func<TextSelectionObservation, CancellationToken, ValueTask> publish)
        {
            _context = context;
            _mainProcessId = checked((uint)mainProcessId);
            _configuration = configuration;
            _publish = publish;
            _mouseHookSubscription = LowLevelHook.CreateMouseHook(MouseHookCallback);
            _detectionWorker = Task.Run(ProcessDetectionsAsync);
        }

        private void MouseHookCallback(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            if (Volatile.Read(ref _isDisposed) != 0) return;
            switch (msg)
            {
                case WINDOW_MESSAGE.WM_LBUTTONDOWN:
                {
                    _isMouseDown = true;
                    _mouseDownPos = hookStruct.pt;
                    _mouseDownTime = Environment.TickCount64;
                    _mouseDownClipboardSequence = PInvoke.GetClipboardSequenceNumber();
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
                // Keep one executing attempt and only the latest pending trigger.
                _pendingDetections.Writer.TryWrite(
                    new DetectionTrigger(
                        currentHwnd,
                        _mouseDownCursor,
                        _mouseUpCursor,
                        _mouseDownClipboardSequence,
                        _reusableCancellationTokenSource.Token));
            }
        }

        private async Task ProcessDetectionsAsync()
        {
            await foreach (var trigger in _pendingDetections.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _isDisposed) != 0) break;
                await BeginDetectAsync(trigger).ConfigureAwait(false);
            }
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
        /// <param name="trigger">Stable input evidence captured for this attempt.</param>
        private async Task BeginDetectAsync(DetectionTrigger trigger)
        {
            try
            {
                // Debounce delay: Wait for multi-click sequence to settle.
                // If user double clicks, we likely don't want to trigger immediately if they are about to triple click.
                await Task.Delay(TimeSpan.FromMilliseconds(DOUBLE_CLICK_TIME_MS), trigger.CancellationToken);

                // If cancellation requested, it means another click happened, we should abort this detection.
                if (trigger.CancellationToken.IsCancellationRequested) return;

                await DetectAsync(trigger);
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

        private async Task DetectAsync(DetectionTrigger trigger)
        {
            var cancellationToken = trigger.CancellationToken;
            var hWnd = trigger.WindowHandle;
            TextSelectionSource? source = null;
            ProcessIdentity? process = null;
            var phase = "target validation";
            try
            {
                process = GetProcessInformationByHwnd(hWnd);
                if (process is null) return;
                var configuration = Volatile.Read(ref _configuration);
                if (cancellationToken.IsCancellationRequested) return;

                if (process.Value.ProcessId == _mainProcessId ||
                    MatchesApplication(configuration.ExcludedApplications, process.Value))
                {
                    return;
                }
                if (configuration.IsFullscreenApplicationExcluded && IsFullscreenTarget(hWnd, process.Value.ProcessId)) return;

                phase = "accessibility read";
                var selectedText = default(TextSelectionText);
                var controlType = UIAutomationControlType.Unknown;

                // 1. Try to get selection from element
                // Console.WriteLine("1. TryGetSelectionTextFromElement");
                try
                {
                    var result = await _context.ExecuteAsync(
                        (visualContext, backend, token) =>
                        {
                            VisualElementRetention? retention = visualContext.CreateRetention();
                            try
                            {
                                var queryResult = backend.Query(retention, VisualElementLocator.Focused);
                                if (queryResult?.Snapshot.ProcessId is not { } processId ||
                                    unchecked((uint)processId) != process.Value.ProcessId)
                                {
                                    retention.Dispose();
                                    return (Source: null, ControlType: UIAutomationControlType.Unknown, Text: null);
                                }

                                token.ThrowIfCancellationRequested();
                                var observedControlType = queryResult.Element is UIAutomationVisualElement automationElement ?
                                    automationElement.ControlType :
                                    UIAutomationControlType.Unknown;
                                var observedText = queryResult.Element.GetSelectedText(TextSelectionTextBudget.MaximumNativeReadCharacters);
                                var observedSource = new TextSelectionSource(retention, queryResult);
                                retention = null;
                                return (
                                    Source: (TextSelectionSource?)observedSource,
                                    ControlType: observedControlType,
                                    Text: observedText);
                            }
                            finally
                            {
                                retention?.Dispose();
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                    source = result.Source;
                    controlType = result.ControlType;
                    if (!string.IsNullOrEmpty(result.Text))
                    {
                        selectedText = TextSelectionTextBudget.Apply(
                            result.Text,
                            result.Text.Length >= TextSelectionTextBudget.MaximumNativeReadCharacters);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is
                                               COMException { ErrorCode: unchecked((int)0x80040201) } or
                                               UnauthorizedAccessException or
                                               TimeoutException or
                                               NotSupportedException or
                                               VisualElementProviderException)
                {
                    Log.ForContext<TextSelectionDetector>()
                        .ForContext("Application", process.Value.ProcessName)
                        .Debug(ex, "Accessibility selection read failed; evaluating clipboard fallback");
                }
                if (cancellationToken.IsCancellationRequested) return;

                // 2. Fallback to Clipboard
                configuration = Volatile.Read(ref _configuration);
                phase = "clipboard fallback";
                if (!selectedText.HasText &&
                    !ClipboardFallbackExcludedProcessNames.Contains(process.Value.ProcessName) &&
                    !MatchesApplication(configuration.ExcludedApplications, process.Value) &&
                    (!configuration.IsFullscreenApplicationExcluded || !IsFullscreenTarget(hWnd, process.Value.ProcessId)) &&
                    ShouldProcessViaClipboard(controlType, process.Value.ProcessName, trigger.MouseDownCursor, trigger.MouseUpCursor))
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    // Console.WriteLine("2. GetTextViaClipboardAsync");
                    selectedText = await GetTextViaClipboardAsync(
                        process.Value.ProcessName,
                        hWnd,
                        process.Value.ProcessId,
                        trigger.ClipboardSequence,
                        cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested) return;

                if (selectedText.Text is { Length: > 0 } text)
                {
                    phase = "notification publish";
                    TextSelectionObservation? observation = new(_context, text, source, selectedText.IsIncomplete);
                    source = null;
                    try
                    {
                        await _publish(observation, cancellationToken).ConfigureAwait(false);
                        observation = null;
                    }
                    finally
                    {
                        if (observation is not null) await observation.DisposeAsync().ConfigureAwait(false);
                    }
                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                var isExpected = ex is
                    COMException { ErrorCode: unchecked((int)0x80040201) } or // COMException: 事件无法调用任何订户 (0x80040201)
                    UnauthorizedAccessException or
                    TimeoutException or
                    VisualElementProviderException;

                if (isExpected)
                {
                    Log.ForContext<TextSelectionDetector>()
                        .ForContext("Application", process?.ProcessName)
                        .Debug(ex, "Text-selection detection could not complete during {Phase}", phase);
                }
                else
                {
                    Log.ForContext<TextSelectionDetector>()
                        .ForContext("Application", process?.ProcessName)
                        .Warning(ex, "Unexpected text-selection failure during {Phase}", phase);
                }
            }
            finally
            {
                if (source is not null) await _context.ReleaseAsync(source).ConfigureAwait(false);
            }
        }

        public ValueTask UpdateConfigurationAsync(
            TextSelectionMonitoringConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _configuration, configuration);
            _reusableCancellationTokenSource.Cancel();
            return ValueTask.CompletedTask;
        }

        private static ProcessIdentity? GetProcessInformationByHwnd(HWND hWnd)
        {
            if (hWnd == HWND.Null) return null;

            PInvoke.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return null;

            var hProcess = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            using var safeProcessHandle = new SafeProcessHandle(hProcess, true);

            var buffer = new char[32_768].AsSpan();
            var size = (uint)buffer.Length;
            var result = PInvoke.QueryFullProcessImageName(safeProcessHandle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size);

            if (!result || size == 0) return null;

            var fullPath = new string(buffer[..(int)size]);
            return new ProcessIdentity(pid, Path.GetFileName(fullPath), fullPath);
        }

        private static bool MatchesApplication(IEnumerable<string> applications, ProcessIdentity process) =>
            applications.Any(application =>
                string.Equals(application, process.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(application, process.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        private static unsafe bool IsFullscreenTarget(HWND targetWindow, uint processId)
        {
            if (!IsForegroundTarget(targetWindow, processId)) return false;

            var root = PInvoke.GetAncestor(targetWindow, GET_ANCESTOR_FLAGS.GA_ROOT);
            if (root == HWND.Null) root = targetWindow;
            if (PInvoke.IsZoomed(root)) return false;

            var bounds = default(RECT);
            if (PInvoke.DwmGetWindowAttribute(
                    root,
                    DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                    &bounds,
                    (uint)sizeof(RECT)).Failed)
            {
                if (!PInvoke.GetWindowRect(root, out bounds)) return false;
            }

            var monitor = PInvoke.MonitorFromWindow(root, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
            if (monitor.IsNull) return false;
            var monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (!PInvoke.GetMonitorInfo(monitor, ref monitorInfo)) return false;

            const int Tolerance = 2;
            var monitorBounds = monitorInfo.rcMonitor;
            return Math.Abs(bounds.left - monitorBounds.left) <= Tolerance &&
                Math.Abs(bounds.top - monitorBounds.top) <= Tolerance &&
                Math.Abs(bounds.right - monitorBounds.right) <= Tolerance &&
                Math.Abs(bounds.bottom - monitorBounds.bottom) <= Tolerance;
        }

        /// <summary>
        /// Check if we should process GetTextViaClipboard
        /// </summary>
        /// <returns></returns>
        private static bool ShouldProcessViaClipboard(
            UIAutomationControlType controlType,
            string processName,
            HCURSOR mouseDownCursor,
            HCURSOR mouseUpCursor)
        {
            // when mouse down or up, any one of them is beamCursor, we can use clipboard
            // otherwise, we have to check the situation further

            // Load common cursors every time to avoid caching issues
            var cursorIBeam = PInvoke.LoadCursor(default, PInvoke.IDC_IBEAM);
            var cursorArrow = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW);
            var cursorHand = PInvoke.LoadCursor(default, PInvoke.IDC_HAND);

            // beam cursor detected: valid text selection
            if (mouseDownCursor == cursorIBeam || mouseUpCursor == cursorIBeam) return true;

            // not beam, not arrow, not hand: invalid text selection cursor
            if (mouseUpCursor != cursorArrow && mouseUpCursor != cursorHand)
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
            return controlType is UIAutomationControlType.Unknown or UIAutomationControlType.Group or UIAutomationControlType.Document or
                UIAutomationControlType.Text;
        }

        private async static Task<TextSelectionText> GetTextViaClipboardAsync(
            string processName,
            HWND targetWindow,
            uint processId,
            uint gestureClipboardSequence,
            CancellationToken cancellationToken)
        {
            if (PInvoke.GetClipboardSequenceNumber() != gestureClipboardSequence) return default;

            // 1. User Intent Check (Avoid interfering with user copy)
            TextSelectionText text;
            if (ShouldAbortForUserIntent())
            {
                return default;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Backup Clipboard
            // We backup Text or Image (CF_DIB) to match standard behavior (like arboard)
            // This prevents losing screenshots when a text selection fallback occurs.
            byte[]? backupData = null;
            uint backupFormat = 0;
            uint? copiedClipboardSequence = null;
            var hasRestorableBackup = false;

            if (PInvoke.OpenClipboard(MessageWindow.Shared.HWnd))
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

                // An empty clipboard can be restored. Clipboard content made exclusively of unsupported
                // formats cannot, so skip synthetic copy rather than knowingly destroying it. EnumClipboardFormats
                // uses the last-error value to distinguish an empty clipboard from enumeration failure.
                Marshal.SetLastPInvokeError(0);
                var firstFormat = PInvoke.EnumClipboardFormats(0);
                hasRestorableBackup = backupFormat != 0 ||
                    firstFormat == 0 && Marshal.GetLastPInvokeError() == 0;

                // Note: We don't empty here, as that might clear file handles or other formats we didn't backup.
                // We rely on the Copy command to overwrite ownership.
                PInvoke.CloseClipboard();
            }

            if (!hasRestorableBackup) return default;

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
                        return default;
                    }
                    if (!IsForegroundTarget(targetWindow, processId)) return default;

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
                        copiedClipboardSequence = PInvoke.GetClipboardSequenceNumber();
                        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
                        copiedClipboardSequence = PInvoke.GetClipboardSequenceNumber();

                        if (!IsForegroundTarget(targetWindow, processId)) return default;
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
                    return default;
                }

                // 4. Strategy B: Ctrl + C (Fallback)
                if (!NoCtrlCProcessNames.Contains(processName))
                {
                    if (!IsForegroundTarget(targetWindow, processId)) return default;
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
                        return default;
                    }

                    copiedClipboardSequence = PInvoke.GetClipboardSequenceNumber();
                }
                else if (copiedClipboardSequence is null)
                {
                    return default;
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
                    return default;
                }

                // Final Attempt
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsForegroundTarget(targetWindow, processId)) return default;
                if (TryGetClipboardText(out text, emptyClipboard: false))
                {
                    copiedClipboardSequence = PInvoke.GetClipboardSequenceNumber();
                    // Console.WriteLine($"Strategy A: TryGetClipboardText: {text}");
                    return text;
                }
            }
            finally
            {
                // 5. Restore Clipboard
                if (copiedClipboardSequence is { } sequence)
                {
                    await MessageWindow.Shared.InvokeAsync(() => RestoreClipboard(sequence, backupFormat, backupData)).ConfigureAwait(false);
                }
            }

            return text;
        }

        private static bool IsForegroundTarget(HWND targetWindow, uint processId)
        {
            var foregroundWindow = PInvoke.GetForegroundWindow();
            if (foregroundWindow == HWND.Null) return false;

            var targetRoot = PInvoke.GetAncestor(targetWindow, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
            var foregroundRoot = PInvoke.GetAncestor(foregroundWindow, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
            if (targetRoot != HWND.Null && targetRoot == foregroundRoot) return true;

            PInvoke.GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
            return foregroundProcessId == processId;
        }

        /// <summary>
        /// Check if user is trying to copy text manually, to avoid interfering.
        /// </summary>
        /// <returns>true to abort the clipboard strategy</returns>
        private static bool ShouldAbortForUserIntent()
        {
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
                    return true;
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
        private static unsafe bool TryGetClipboardText(
            out TextSelectionText text,
            bool emptyClipboard = false,
            bool openClipboard = true)
        {
            text = default;
            if (openClipboard && !PInvoke.OpenClipboard(MessageWindow.Shared.HWnd)) return false;

            try
            {
                var handle = PInvoke.GetClipboardData(CF_UNICODETEXT);
                if (handle.Value == null) return false;

                var ptr = PInvoke.GlobalLock((HGLOBAL)handle.Value);
                if (ptr != null)
                {
                    var availableCharacters = checked((int)Math.Min(
                        PInvoke.GlobalSize((HGLOBAL)handle.Value) / sizeof(char),
                        int.MaxValue));
                    var inspectedCharacters = Math.Min(
                        availableCharacters,
                        TextSelectionTextBudget.MaximumNativeReadCharacters);
                    var characters = new ReadOnlySpan<char>(ptr, inspectedCharacters);
                    var terminatorIndex = characters.IndexOf('\0');
                    var length = terminatorIndex >= 0 ? terminatorIndex : inspectedCharacters;
                    var value = new string(characters[..length]);
                    text = TextSelectionTextBudget.Apply(
                        value,
                        terminatorIndex < 0 && availableCharacters > inspectedCharacters);
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

            // Clipboard ownership transfers only when SetClipboardData succeeds.
            var hMem = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE | GLOBAL_ALLOC_FLAGS.GMEM_ZEROINIT, sizeof(int));
            if (hMem == 0) return;
            if (PInvoke.SetClipboardData(cfFormat, (HANDLE)hMem.Value).Value == null) PInvoke.GlobalFree(hMem);
        }

        private static bool RestoreClipboard(uint expectedSequence, uint format, byte[]? data)
        {
            if (!PInvoke.OpenClipboard(MessageWindow.Shared.HWnd)) return false;

            try
            {
                if (PInvoke.GetClipboardSequenceNumber() != expectedSequence) return false;
                PInvoke.EmptyClipboard();
                if (format != 0 && data is not null && !SetClipboardDataCore(format, data)) return false;

                // Prevent this internal restoration from entering clipboard history or cloud sync.
                SetClipboardExclusion(ref _cfHistory, "CanIncludeInClipboardHistory");
                SetClipboardExclusion(ref _cfCloud, "CanUploadToCloudClipboard");
                return true;
            }
            finally
            {
                PInvoke.CloseClipboard();
            }
        }

        private static unsafe bool SetClipboardDataCore(uint format, byte[] data)
        {
            var hGlobal = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
            if (hGlobal == 0) return false;

            var targetPtr = PInvoke.GlobalLock(hGlobal);
            if (targetPtr == null)
            {
                PInvoke.GlobalFree(hGlobal);
                return false;
            }

            Marshal.Copy(data, 0, (nint)targetPtr, data.Length);
            PInvoke.GlobalUnlock(hGlobal);
            if (PInvoke.SetClipboardData(format, (HANDLE)hGlobal.Value).Value != null) return true;

            PInvoke.GlobalFree(hGlobal);
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            _mouseHookSubscription.Dispose();
            _reusableCancellationTokenSource.Cancel();
            _pendingDetections.Writer.TryComplete();
            try
            {
                await _detectionWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _reusableCancellationTokenSource.Dispose();
            }
        }

        private readonly record struct DetectionTrigger(
            HWND WindowHandle,
            HCURSOR MouseDownCursor,
            HCURSOR MouseUpCursor,
            uint ClipboardSequence,
            CancellationToken CancellationToken
        );

        private readonly record struct ProcessIdentity(uint ProcessId, string ProcessName, string ExecutablePath);
    }
}