using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Everywhere.Automation;
using Everywhere.Extensions;
using Everywhere.Interop;
using Serilog;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Monitors text selection through macOS accessibility, input, and clipboard facilities.
/// </summary>
public sealed class MacTextSelectionWatcher(IVisualElementBackend visualElementBackend) : ITextSelectionWatcher
{
    private readonly Subject<TextSelectionData> _textSelectionSubject = new();
    private IDisposable? _hookSubscription;
    private int _subscriberCount;

    /// <inheritdoc />
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

        var detector = new TextSelectionDetector(visualElementBackend);
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

        // Atomic flags
        private volatile int _isProcessing; // 0 = false, 1 = true

        private readonly IVisualElementBackend _visualElementBackend;

        private DateTimeOffset _lastMouseDownTime, _lastMouseUpTime;
        private CGPoint _lastMouseDownPos, _lastMouseUpPos;
        private bool _isLastIBeamCursor, _isLastValidClick;
        private long _clipboardSequence;

        private const int MinDragDistance = 8;
        private const int MaxDragTimeMilliseconds = 15_000;
        private const int DoubleClickMaxDistance = 3;
        private const int DoubleClickTimeMilliseconds = 500;
        private const int MaximumSelectedTextCharacters = 65_536;
        private const int MaximumSelectedTextChildProbes = 32;

        private static readonly HashSet<string> ExcludeProcessNames = new(StringComparer.OrdinalIgnoreCase);

        public TextSelectionDetector(IVisualElementBackend visualElementBackend)
        {
            _visualElementBackend = visualElementBackend;
            CGEventListener.ListenOnly.EventReceived += HandleEvent;
        }

        private void HandleEvent(CGEventType type, CGEvent cgEvent, ref IntPtr cgEventRef)
        {
            // Only care about left mouse button events
            if (type is not CGEventType.LeftMouseDown and not CGEventType.LeftMouseUp) return;

            var isIBeamCursor = IsIBeamCursor();
            if (type == CGEventType.LeftMouseDown)
            {
                _lastMouseDownTime = DateTimeOffset.Now;
                _lastMouseDownPos = cgEvent.Location;
                _isLastIBeamCursor = isIBeamCursor;
                _clipboardSequence = GetClipboardSequence();
                return;
            }

            var shouldDetectSelection = false;

            // Calculate distance between current position and mouse down position
            var dx = cgEvent.Location.X - _lastMouseDownPos.X;
            var dy = cgEvent.Location.Y - _lastMouseDownPos.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);

            var currentTime = DateTimeOffset.Now;
            var isCurrentClickValid = (currentTime - _lastMouseDownTime).TotalMilliseconds <= DoubleClickTimeMilliseconds;
            var isCursorValid = _isLastIBeamCursor || isIBeamCursor;

            if ((currentTime - _lastMouseDownTime).TotalMilliseconds > MaxDragTimeMilliseconds)
            {
                shouldDetectSelection = false;
            }
            // Check for drag selection
            else if (distance >= MinDragDistance)
            {
                // Only support IBeamCursor for now
                if (isCursorValid)
                {
                    shouldDetectSelection = true;
                }
            }
            // Check for double-click selection
            else if (_isLastValidClick && isCurrentClickValid && distance <= DoubleClickMaxDistance)
            {
                var dx2 = cgEvent.Location.X - _lastMouseUpPos.X;
                var dy2 = cgEvent.Location.Y - _lastMouseUpPos.Y;
                var distance2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);

                if (distance2 <= DoubleClickMaxDistance &&
                    (_lastMouseDownTime - _lastMouseUpTime).TotalMilliseconds <= DoubleClickTimeMilliseconds)
                {
                    // Only support IBeamCursor for now
                    if (isCursorValid)
                    {
                        shouldDetectSelection = true;
                    }
                }
            }

            // Check if shift key is pressed when mouse up, it's a way to select text
            if (!shouldDetectSelection)
            {
                // Get current event flags to check for shift key
                var flags = cgEvent.Flags;
                var isShiftPressed = (flags & CGEventFlags.Shift) != 0;
                var isCtrlPressed = (flags & CGEventFlags.Control) != 0;
                var isCmdPressed = (flags & CGEventFlags.Command) != 0;
                var isOptionPressed = (flags & CGEventFlags.Alternate) != 0;

                if (isShiftPressed && !isCtrlPressed && !isCmdPressed && !isOptionPressed)
                {
                    // Only support IBeamCursor for now
                    if (isCursorValid)
                    {
                        shouldDetectSelection = true;
                    }
                }
            }

            _isLastValidClick = isCurrentClickValid;
            _lastMouseUpTime = currentTime;
            _lastMouseUpPos = cgEvent.Location;

            if (shouldDetectSelection)
            {
                BeginDetect();
            }
        }

        /// <summary>
        /// Begin detection of text selection.
        /// </summary>
        private void BeginDetect()
        {
            NSRunningApplication frontApp;
            try
            {
                frontApp = GetFrontApp();
            }
            catch (Exception ex)
            {
                Log.ForContext<TextSelectionDetector>().Error(ex, "Failed to get frontmost application");
                return;
            }

            var processName = frontApp.BundleIdentifier ?? string.Empty;
            if (ExcludeProcessNames.Contains(processName)) return;

            var pid = frontApp.ProcessIdentifier;
            if (pid == Environment.ProcessId) return;

            // Check processing flag
            if (_isProcessing == 1) return;

            // Offload to thread pool to avoid blocking the hook
            Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 1) return;

                try
                {
                    // 1. Try to get selection from element (Priority 1)
                    var text = GetTextViaAXAPI(pid, out var locator);

                    // 2. Fallback to Clipboard (Priority 3)
                    if (string.IsNullOrEmpty(text))
                    {
                        text = await GetTextViaClipboardAsync(pid);
                    }

                    // Trigger event whatever we got
                    // A null or empty text indicates selection was canceled or failed
                    SelectionDetected?.Invoke(new TextSelectionData(text, locator));
                }
                catch (Exception ex)
                {
                    // Ignore errors during detection
                    Log.ForContext<TextSelectionDetector>().Warning(ex, "Error during text selection detection");
                }
                finally
                {
                    Interlocked.Exchange(ref _isProcessing, 0);
                }
            }).Detach();
        }

        private string? GetTextViaAXAPI(int processId, out VisualElementLocator? locator)
        {
            locator = null;
            using var context = new VisualContext();
            using var retention = context.CreateRetention();
            try
            {
                var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.ProcessId, 0);
                var result = _visualElementBackend.Query(retention, VisualElementLocator.Focused, VisualElementResolution.Direct, request);
                if (result?.Snapshot.ProcessId != processId) return null;

                locator = VisualElementLocator.Focused;
                var text = result.Element.GetSelectedText(MaximumSelectedTextCharacters);
                if (!string.IsNullOrEmpty(text)) return text;

                // Some providers expose the selection only on a direct child of the focused container. Preserve
                // that legacy behavior, but cap the number of synchronous AX messages in one detection attempt.
                var childRequest = new VisualElementQueryRequest(VisualElementFields.Id, 0);
                using var children = result.Element.CreateEnumerator(
                    VisualElementRelation.Child,
                    childRequest);
                for (var index = 0; index < MaximumSelectedTextChildProbes && children.MoveNext(); index++)
                {
                    text = children.Current.Element.GetSelectedText(MaximumSelectedTextCharacters);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch (Exception ex)
            {
                Log.ForContext<TextSelectionDetector>().Debug(ex, "Could not read selected text through the macOS Accessibility backend");
            }

            using var applicationElement = AXUIElement.ElementFromPid(processId);
            if (applicationElement is not null)
            {
                // Chrome/Chromium: set "AXEnhancedUserInterface" to true to enable AXAPI.
                using var enabled = NSNumber.FromBoolean(true);
                applicationElement.SetAttribute(AXAttributeConstants.EnhancedUserInterface, enabled);
                // Electron Apps: set "AXManualAccessibility" to true to enable AXAPI.
                applicationElement.SetAttribute(AXAttributeConstants.ManualAccessibility, enabled);
            }

            return null;
        }

        private async ValueTask<string?> GetTextViaClipboardAsync(int pid)
        {
            string? text = null;
            var newClipboardSequence = GetClipboardSequence();

            // Check if clipboard sequence number has changed since mouse down
            // if it's changed, it means user has copied something, we can read it directly
            if (newClipboardSequence == _clipboardSequence)
            {
                text = ReadClipboard();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            var originalClipboardContent = ReadClipboard();

            try
            {
                await SendCopyKeyAsync(pid);
            }
            catch
            {
                return null;
            }

            // Check clipboard sequence number in a loop with 10ms interval
            // This gives the copy operation up to 100ms to complete
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                newClipboardSequence = GetClipboardSequence();
                if (newClipboardSequence == _clipboardSequence) continue;

                text = ReadClipboard();
                break;
            }

            // Restore original clipboard content
            if (!string.IsNullOrEmpty(originalClipboardContent))
            {
                WriteClipboard(originalClipboardContent);
            }

            return text;
        }

        private static async ValueTask SendCopyKeyAsync(int pid)
        {
            using var keyDownEvent = new CGEvent(null, (ushort)CGKeyCode.C, true);
            keyDownEvent.Flags |= CGEventFlags.Command;

            using var keyUpEvent = new CGEvent(null, (ushort)CGKeyCode.C, false);
            keyUpEvent.Flags |= CGEventFlags.Command;

            if (pid != 0)
            {
                CGEvent.PostToPid(keyDownEvent, pid);
                await Task.Delay(5);
                CGEvent.PostToPid(keyUpEvent, pid);
            }
            else
            {
                CGEvent.Post(keyDownEvent, CGEventTapLocation.HID);
                await Task.Delay(5);
                CGEvent.Post(keyUpEvent, CGEventTapLocation.HID);
            }
        }

        /// <summary>
        /// Check if cursor is I-beam cursor by comparing hotSpot
        /// </summary>
        /// <remarks>
        /// WTF? So hacky!
        /// </remarks>
        /// <returns></returns>
        private static bool IsIBeamCursor()
        {
            using var pool = new NSAutoreleasePool();
#pragma warning disable CA1422 // NSCursor.CurrentCursor is different from NSCursor.CurrentSystemCursor
            using var current = NSCursor.CurrentSystemCursor;
#pragma warning restore CA1422
            if (current is null) return false;
            var iBeam = NSCursor.IBeamCursor;
            return current.HotSpot == iBeam.HotSpot;
        }

        private static string? ReadClipboard()
        {
            try
            {
                using var pool = new NSAutoreleasePool();
                var pasteboard = NSPasteboard.GeneralPasteboard;
#pragma warning disable CS0618
                var contentString = pasteboard.GetStringForType(NSPasteboard.NSPasteboardTypeString);
#pragma warning restore CS0618
                return contentString;
            }
            catch
            {
                return null;
            }
        }

        private static bool WriteClipboard(string content)
        {
            if (string.IsNullOrEmpty(content))
                return false;

            try
            {
                using var pool = new NSAutoreleasePool();
                var pasteboard = NSPasteboard.GeneralPasteboard;
                pasteboard.ClearContents();
                var contentString = new NSString(content);
#pragma warning disable CS0618
                var success = pasteboard.SetStringForType(contentString, NSPasteboard.NSPasteboardTypeString);
#pragma warning restore CS0618
                return success;
            }
            catch
            {
                return false;
            }
        }

        private static long GetClipboardSequence()
        {
            using var pool = new NSAutoreleasePool();
            var pasteboard = NSPasteboard.GeneralPasteboard;
            return pasteboard.ChangeCount;
        }

        private static NSRunningApplication GetFrontApp()
        {
            using var pool = new NSAutoreleasePool();
            NSRunLoop.Current.RunUntil(NSRunLoopMode.Default, NSDate.DistantPast);
            var workspace = NSWorkspace.SharedWorkspace;
            return workspace.FrontmostApplication;
        }

        public void Dispose()
        {
            CGEventListener.ListenOnly.EventReceived -= HandleEvent;
        }
    }
}
