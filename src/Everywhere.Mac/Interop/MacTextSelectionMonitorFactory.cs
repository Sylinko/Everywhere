using System.Threading.Channels;
using Everywhere.Automation;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Monitors text selection through macOS accessibility, input, and clipboard facilities.
/// </summary>
public sealed class MacTextSelectionMonitorFactory : ITextSelectionMonitorFactory
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
        private readonly ReusableCancellationTokenSource _detectionCancellation = new();
        private readonly Task _detectionWorker;
        private readonly int _mainProcessId;
        private readonly Channel<DetectionTrigger> _pendingDetections = Channel.CreateBounded<DetectionTrigger>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private readonly Func<TextSelectionObservation, CancellationToken, ValueTask> _publish;

        private TextSelectionMonitoringConfiguration _configuration;
        private int _isDisposed;

        private DateTimeOffset _lastMouseDownTime, _lastMouseUpTime;
        private CGPoint _lastMouseDownPos, _lastMouseUpPos;
        private bool _isLastIBeamCursor, _isLastValidClick;
        private long _clipboardSequence;

        private const int MinDragDistance = 8;
        private const int MaxDragTimeMilliseconds = 15_000;
        private const int DoubleClickMaxDistance = 3;
        private const int DoubleClickTimeMilliseconds = 500;
        private const int MaximumSelectedTextChildProbes = 32;

        private static readonly HashSet<string> AccessibilityOnlyApplications = new(StringComparer.Ordinal)
        {
            "com.apple.Terminal",
            "com.googlecode.iterm2",
            "com.github.wez.wezterm",
            "dev.warp.Warp-Stable",
            "net.kovidgoyal.kitty",
            "org.alacritty",
        };

        public TextSelectionDetector(
            ITextSelectionMonitorContext context,
            int mainProcessId,
            TextSelectionMonitoringConfiguration configuration,
            Func<TextSelectionObservation, CancellationToken, ValueTask> publish)
        {
            _context = context;
            _mainProcessId = mainProcessId;
            _configuration = configuration;
            _publish = publish;
            CGEventListener.ListenOnly.EventReceived += HandleEvent;
            _detectionWorker = Task.Run(ProcessDetectionsAsync);
        }

        private void HandleEvent(CGEventType type, CGEvent cgEvent, ref IntPtr cgEventRef)
        {
            if (Volatile.Read(ref _isDisposed) != 0) return;
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
            var configuration = Volatile.Read(ref _configuration);
            if (MatchesApplication(configuration.ExcludedApplications, processName)) return;
            if (configuration.IsFullscreenApplicationExcluded && IsFullscreenTarget(frontApp)) return;

            var pid = frontApp.ProcessIdentifier;
            if (pid == _mainProcessId) return;

            _detectionCancellation.Cancel();
            _pendingDetections.Writer.TryWrite(
                new DetectionTrigger(pid, processName, _clipboardSequence, _detectionCancellation.Token));
        }

        private async Task ProcessDetectionsAsync()
        {
            await foreach (var trigger in _pendingDetections.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _isDisposed) != 0) break;
                await DetectAsync(trigger, trigger.CancellationToken).ConfigureAwait(false);
            }
        }

        private async Task DetectAsync(DetectionTrigger trigger, CancellationToken cancellationToken)
        {
            TextSelectionSource? source = null;
            var phase = "target validation";
            try
            {
                var configuration = Volatile.Read(ref _configuration);
                if (!IsCurrentTarget(trigger.ProcessId, trigger.ApplicationId) ||
                    MatchesApplication(configuration.ExcludedApplications, trigger.ApplicationId) ||
                    (configuration.IsFullscreenApplicationExcluded && IsFullscreenTarget(GetFrontApp()))) return;

                phase = "accessibility read";
                // 1. Try to get selection from element (Priority 1)
                var accessibilityResult = await GetTextViaAXAPIAsync(trigger.ProcessId, cancellationToken).ConfigureAwait(false);
                var selectedText = !string.IsNullOrEmpty(accessibilityResult.Text) ?
                    TextSelectionTextBudget.Apply(
                        accessibilityResult.Text,
                        accessibilityResult.Text.Length >= TextSelectionTextBudget.MaximumNativeReadCharacters) :
                    default;
                source = accessibilityResult.Source;

                // 2. Fallback to Clipboard (Priority 3)
                configuration = Volatile.Read(ref _configuration);
                phase = "clipboard fallback";
                if (!selectedText.HasText &&
                    !AccessibilityOnlyApplications.Contains(trigger.ApplicationId) &&
                    !MatchesApplication(configuration.ExcludedApplications, trigger.ApplicationId) &&
                    (!configuration.IsFullscreenApplicationExcluded || !IsFullscreenTarget(GetFrontApp())) &&
                    IsCurrentTarget(trigger.ProcessId, trigger.ApplicationId))
                {
                    var clipboardText = await GetTextViaClipboardAsync(
                        trigger.ProcessId,
                        trigger.ClipboardSequence,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(clipboardText)) selectedText = TextSelectionTextBudget.Apply(clipboardText);
                }

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
                if (ex is not (UnauthorizedAccessException or TimeoutException or NotSupportedException or VisualElementProviderException))
                {
                    Log.ForContext<TextSelectionDetector>()
                        .ForContext("Application", trigger.ApplicationId)
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
            _detectionCancellation.Cancel();
            return ValueTask.CompletedTask;
        }

        private async ValueTask<(string? Text, TextSelectionSource? Source)> GetTextViaAXAPIAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            (string? Text, TextSelectionSource? Source) observed = default;
            try
            {
                observed = await _context.ExecuteAsync(
                    (visualContext, backend, token) =>
                    {
                        var retention = visualContext.CreateRetention();
                        try
                        {
                            var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.ProcessId, 0);
                            var result = backend.Query(retention, VisualElementLocator.Focused, VisualElementResolution.Direct, request);
                            if (result?.Snapshot.ProcessId != processId) return default;

                            token.ThrowIfCancellationRequested();
                            var text = result.Element.GetSelectedText(TextSelectionTextBudget.MaximumNativeReadCharacters);
                            if (!string.IsNullOrEmpty(text))
                            {
                                var source = new TextSelectionSource(retention, result);
                                retention = null;
                                return (text, source);
                            }

                            // Some providers expose the selection only on a direct child of the focused container.
                            var childRequest = new VisualElementQueryRequest(VisualElementFields.Id, 0);
                            using var children = result.Element.CreateEnumerator(VisualElementRelation.Child, childRequest, cancellationToken: token);
                            for (var index = 0; index < MaximumSelectedTextChildProbes && children.MoveNext(); index++)
                            {
                                var child = children.Current;
                                if (!child.IsSuccess) break;
                                text = child.Result.Element.GetSelectedText(TextSelectionTextBudget.MaximumNativeReadCharacters);
                                if (string.IsNullOrEmpty(text)) continue;
                                retention.Retain(child.Result.Element);
                                var source = new TextSelectionSource(retention, child.Result);
                                retention = null;
                                return (text, source);
                            }

                            var fallbackSource = new TextSelectionSource(retention, result);
                            retention = null;
                            return ((string?)null, fallbackSource);
                        }
                        finally
                        {
                            retention?.Dispose();
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(observed.Text)) return observed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or TimeoutException or NotSupportedException or VisualElementProviderException)
            {
                // Log.ForContext<TextSelectionDetector>().Debug(ex, "Could not read selected text through the macOS Accessibility backend");
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

            return observed;
        }

        private static async ValueTask<string?> GetTextViaClipboardAsync(
            int pid,
            long mouseDownClipboardSequence,
            CancellationToken cancellationToken)
        {
            var newClipboardSequence = GetClipboardSequence();
            if (newClipboardSequence != mouseDownClipboardSequence) return null;
            if (!TryReadClipboard(out var originalClipboardContent)) return null;
            var originalClipboardSequence = newClipboardSequence;
            long? copiedClipboardSequence = null;
            string? text = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetFrontApp().ProcessIdentifier != pid) return null;
                await SendCopyKeyAsync(pid, cancellationToken).ConfigureAwait(false);
                // Check clipboard sequence number in a loop with 10ms interval.
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    newClipboardSequence = GetClipboardSequence();
                    if (newClipboardSequence == originalClipboardSequence) continue;

                    copiedClipboardSequence = newClipboardSequence;
                    TryReadClipboard(out text);
                    break;
                }

                if (GetFrontApp().ProcessIdentifier != pid) return null;
                return text;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                // Restore only while this copy attempt still owns the current clipboard value.
                if (copiedClipboardSequence is { } copiedSequence && GetClipboardSequence() == copiedSequence)
                {
                    if (!string.IsNullOrEmpty(originalClipboardContent)) WriteClipboard(originalClipboardContent);
                    else ClearClipboard();
                }
            }
        }

        private static async ValueTask SendCopyKeyAsync(int pid, CancellationToken cancellationToken)
        {
            using var keyDownEvent = new CGEvent(null, (ushort)CGKeyCode.C, true);
            keyDownEvent.Flags |= CGEventFlags.Command;

            using var keyUpEvent = new CGEvent(null, (ushort)CGKeyCode.C, false);
            keyUpEvent.Flags |= CGEventFlags.Command;

            if (pid != 0)
            {
                CGEvent.PostToPid(keyDownEvent, pid);
                try
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CGEvent.PostToPid(keyUpEvent, pid);
                }
            }
            else
            {
                CGEvent.Post(keyDownEvent, CGEventTapLocation.HID);
                try
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CGEvent.Post(keyUpEvent, CGEventTapLocation.HID);
                }
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

        private static bool TryReadClipboard(out string? text)
        {
            try
            {
                using var pool = new NSAutoreleasePool();
                var pasteboard = NSPasteboard.GeneralPasteboard;
#pragma warning disable CS0618
                var contentString = pasteboard.GetStringForType(NSPasteboard.NSPasteboardTypeString);
#pragma warning restore CS0618
                text = contentString;
                return true;
            }
            catch
            {
                text = null;
                return false;
            }
        }

        private static void WriteClipboard(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            try
            {
                using var pool = new NSAutoreleasePool();
                var pasteboard = NSPasteboard.GeneralPasteboard;
                pasteboard.ClearContents();
                var contentString = new NSString(content);
#pragma warning disable CS0618
                pasteboard.SetStringForType(contentString, NSPasteboard.NSPasteboardTypeString);
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                Log.ForContext<MacTextSelectionMonitorFactory>().Information(ex, "Failed to write clipboard.");
            }
        }

        private static void ClearClipboard()
        {
            try
            {
                using var pool = new NSAutoreleasePool();
                NSPasteboard.GeneralPasteboard.ClearContents();
            }
            catch (Exception ex)
            {
                Log.ForContext<MacTextSelectionMonitorFactory>().Information(ex, "Failed to clear clipboard.");
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

        private static bool MatchesApplication(IEnumerable<string> applications, string applicationId) =>
            applications.Any(application => string.Equals(application, applicationId, StringComparison.Ordinal));

        private static bool IsCurrentTarget(int processId, string applicationId)
        {
            var frontApp = GetFrontApp();
            return frontApp.ProcessIdentifier == processId &&
                string.Equals(frontApp.BundleIdentifier ?? string.Empty, applicationId, StringComparison.Ordinal);
        }

        private static bool IsFullscreenTarget(NSRunningApplication target)
        {
            var frontApp = GetFrontApp();
            if (frontApp.ProcessIdentifier != target.ProcessIdentifier) return false;
            return NSApplication.SharedApplication.CurrentSystemPresentationOptions.HasFlag(NSApplicationPresentationOptions.FullScreen);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            CGEventListener.ListenOnly.EventReceived -= HandleEvent;
            _detectionCancellation.Cancel();
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
                _detectionCancellation.Dispose();
            }
        }

        private readonly record struct DetectionTrigger(
            int ProcessId,
            string ApplicationId,
            long ClipboardSequence,
            CancellationToken CancellationToken
        );
    }
}