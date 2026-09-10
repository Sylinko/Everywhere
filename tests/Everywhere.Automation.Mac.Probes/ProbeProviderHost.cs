using AppKit;
using CoreGraphics;
using Foundation;

namespace Everywhere.Automation.Mac.Probes;

internal static class ProbeProviderHost
{
    internal static int Run()
    {
        var application = NSApplication.SharedApplication;
        application.Delegate = new ProbeProviderApplicationDelegate();
        NSApplication.Main([]);
        return 0;
    }

    private sealed class ProbeProviderApplicationDelegate : NSApplicationDelegate
    {
        private const int CollectionItemCount = 130;
        private const int LargeTextLength = 100_000;

        private readonly ManualResetEventSlim _providerGate = new(true);
        private readonly object _outputGate = new();
        private readonly List<NSButton> _collectionButtons = [];
        private NSWindow? _window;
        private NSWindow? _secondaryWindow;
        private NSWindow? _tertiaryWindow;
        private NSPanel? _panel;
        private NSWindow? _sheet;
        private NSTextField? _sheetInput;
        private NSWindow? _contentWindow;
        private NSWindow? _captureWindow;
        private NSWindow? _captureOccluderWindow;
        private NSTextView? _largeTextView;
        private NSView? _collectionGroup;
        private readonly List<NSView> _captureQuadrants = [];
        private CGRect _captureInitialFrame;
        private double _captureBackingScaleFactor;
        private string _capturePlacement = "unprepared";
        private bool _isCaptureOccluded;
        private bool _isCaptureWindowBorderless;
        private Task? _commandTask;

        public override void DidFinishLaunching(NSNotification notification)
        {
            var window = new NSWindow(
                new CGRect(0, 0, 480, 240),
                NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable,
                NSBackingStore.Buffered,
                false)
            {
                Title = "Everywhere AX Probe Provider",
                AccessibilityIdentifier = "probe-window",
            };
            var label = new NSTextField(new CGRect(24, 170, 432, 28))
            {
                StringValue = "Native AppKit accessibility probe",
                Editable = false,
                Bezeled = false,
                DrawsBackground = false,
            };
            var input = new NSTextField(new CGRect(24, 112, 432, 28))
            {
                StringValue = "probe-value",
            };
            var button = new NSButton(new CGRect(24, 48, 180, 32))
            {
                Title = "Probe Button",
            };
            window.ContentView?.AddSubview(label);
            window.ContentView?.AddSubview(input);
            window.ContentView?.AddSubview(button);
            window.Center();
            window.MakeKeyAndOrderFront(null);
            window.MakeFirstResponder(input);
            _window = window;
            _commandTask = Task.Run(ReadCommands);
            WriteStatus($"READY\t{Environment.ProcessId}");
        }

        public override void WillTerminate(NSNotification notification)
        {
            _providerGate.Set();
            CloseWindow(ref _sheet);
            CloseWindow(ref _contentWindow);
            CloseWindow(ref _captureOccluderWindow);
            CloseWindow(ref _captureWindow);
            CloseWindow(ref _panel);
            CloseWindow(ref _tertiaryWindow);
            CloseWindow(ref _secondaryWindow);
            CloseWindow(ref _window);
            _sheetInput = null;
            _largeTextView = null;
            _collectionGroup = null;
            _collectionButtons.Clear();
        }

        private void ReadCommands()
        {
            while (Console.ReadLine() is { } command)
            {
                switch (command)
                {
                    case "inspect-panel":
                    case "panel-key":
                    case "panel-deactivate":
                    case "panel-accessible":
                    case "panel-floating":
                    case "panel-persistent":
                        QueueProviderAction(() => InspectPanel(command));
                        break;
                    case "block":
                        _providerGate.Reset();
                        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
                        {
                            WriteStatus("BLOCKED");
                            _providerGate.Wait();
                            WriteStatus("RESUMED");
                        });
                        break;
                    case "resume":
                        _providerGate.Set();
                        break;
                    case "prepare-window-set":
                        QueueProviderAction(PrepareWindowSet);
                        break;
                    case "order-secondary-front":
                        QueueProviderAction(() => OrderWindowFront(_secondaryWindow, "secondary"));
                        break;
                    case "order-tertiary-front":
                        QueueProviderAction(() => OrderWindowFront(_tertiaryWindow, "tertiary"));
                        break;
                    case "hide-secondary":
                        QueueProviderAction(HideSecondaryWindow);
                        break;
                    case "close-secondary":
                        QueueProviderAction(CloseSecondaryWindow);
                        break;
                    case "minimize-tertiary":
                        QueueProviderAction(MinimizeTertiaryWindow);
                        break;
                    case "show-sheet":
                        QueueProviderAction(ShowSheet);
                        break;
                    case "prepare-content":
                        QueueProviderAction(PrepareContent);
                        break;
                    case "focus-large-text":
                        QueueProviderAction(FocusLargeText);
                        break;
                    case "focus-collection":
                        QueueProviderAction(FocusCollection);
                        break;
                    case "mutate-collection":
                        QueueProviderAction(MutateCollection);
                        break;
                    case "close-content":
                        QueueProviderAction(CloseContent);
                        break;
                    case "prepare-capture":
                        QueueProviderAction(() => PrepareCapture(false));
                        break;
                    case "prepare-capture-borderless":
                        QueueProviderAction(() => PrepareCapture(true));
                        break;
                    case "minimize-capture":
                        QueueProviderAction(MinimizeCapture);
                        break;
                    case "restore-capture":
                        QueueProviderAction(RestoreCapture);
                        break;
                    case "occlude-capture":
                        QueueProviderAction(OccludeCapture);
                        break;
                    case "reveal-capture":
                        QueueProviderAction(RevealCapture);
                        break;
                    case "move-capture-partially-offscreen":
                        QueueProviderAction(() => MoveCaptureOffscreen(false));
                        break;
                    case "move-capture-fully-offscreen":
                        QueueProviderAction(() => MoveCaptureOffscreen(true));
                        break;
                    case "restore-capture-position":
                        QueueProviderAction(RestoreCapturePosition);
                        break;
                    case "exit":
                        _providerGate.Set();
                        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(null));
                        return;
                    default:
                        WriteStatus($"ERROR\tUnknown command: {command}");
                        break;
                }
            }

            _providerGate.Set();
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(null));
        }

        private void PrepareWindowSet()
        {
            var mainWindow = _window ?? throw new InvalidOperationException("The provider main window is unavailable.");
            _secondaryWindow ??= CreateWindow("Everywhere AX Probe Secondary", "probe-secondary-window", new CGRect(180, 180, 360, 180));
            _tertiaryWindow ??= CreateWindow("Everywhere AX Probe Tertiary", "probe-tertiary-window", new CGRect(260, 260, 360, 180));
            if (_panel is null)
            {
                var panel = new NSPanel(
                    new CGRect(340, 340, 320, 160),
                    NSWindowStyle.Titled | NSWindowStyle.Closable,
                    NSBackingStore.Buffered,
                    false)
                {
                    Title = "Everywhere AX Probe Panel",
                    AccessibilityIdentifier = "probe-panel",
                    FloatingPanel = false,
                };
                using var panelLabel = new NSTextField(new CGRect(20, 60, 280, 28))
                {
                    StringValue = "Native AppKit panel probe",
                    Editable = false,
                    Bezeled = false,
                    DrawsBackground = false,
                };
                panel.ContentView?.AddSubview(panelLabel);
                _panel = panel;
            }

            _secondaryWindow.OrderFrontRegardless();
            _tertiaryWindow.OrderFrontRegardless();
            _panel.OrderFrontRegardless();
            ActivateApplication();
            mainWindow.MakeKeyAndOrderFront(null);
            WriteStatus($"WINDOW_SET\t{GetWindowId(mainWindow)}\t{GetWindowId(_secondaryWindow)}\t{GetWindowId(_tertiaryWindow)}\t{GetWindowId(_panel)}");
        }

        private void OrderWindowFront(NSWindow? window, string name)
        {
            if (window is null)
            {
                throw new InvalidOperationException($"The {name} probe window is unavailable.");
            }

            window.OrderFrontRegardless();
            WriteStatus($"ORDERED\t{name}\t{GetWindowId(window)}");
        }

        private void InspectPanel(string command)
        {
            var panel = _panel ?? throw new InvalidOperationException("Prepare the window set before inspecting the panel.");
            if (command == "panel-key") panel.MakeKeyAndOrderFront(null);
            if (command == "panel-deactivate") NSApplication.SharedApplication.Deactivate();
            if (command == "panel-accessible") panel.AccessibilityElement = true;
            if (command == "panel-floating") panel.FloatingPanel = true;
            if (command == "panel-persistent") panel.HidesOnDeactivate = false;
            var frame = panel.Frame;
            var observation = new
            {
                Command = command,
                WindowId = GetWindowId(panel),
                IsVisible = panel.IsVisible,
                IsKey = panel.IsKeyWindow,
                IsMain = panel.IsMainWindow,
                IsApplicationActive = NSApplication.SharedApplication.Active,
                IsAccessibilityElement = panel.AccessibilityElement,
                IsAccessibilityHidden = panel.AccessibilityHidden,
                Role = panel.AccessibilityRole,
                Subrole = panel.AccessibilitySubrole,
                HasHideOnDeactivate = panel.HidesOnDeactivate,
                IsFloating = panel.FloatingPanel,
                X = (double)(frame.X + frame.Width / 2),
                Y = (double)(NSScreen.Screens[0].Frame.Height - frame.Y - frame.Height / 2),
            };
            WriteStatus("PANEL\t" + System.Text.Json.JsonSerializer.Serialize(observation));
        }

        private void HideSecondaryWindow()
        {
            var window = _secondaryWindow ?? throw new InvalidOperationException("The secondary probe window is unavailable.");
            window.OrderOut(null);
            WriteStatus($"HIDDEN\t{GetWindowId(window)}");
        }

        private void MinimizeTertiaryWindow()
        {
            var window = _tertiaryWindow ?? throw new InvalidOperationException("The tertiary probe window is unavailable.");
            window.Miniaturize(null);
            WriteStatus($"MINIMIZED\t{GetWindowId(window)}");
        }

        private void CloseSecondaryWindow()
        {
            var window = _secondaryWindow ?? throw new InvalidOperationException("The secondary probe window is unavailable.");
            var windowId = GetWindowId(window);
            CloseWindow(ref _secondaryWindow);
            WriteStatus($"CLOSED_SECONDARY\t{windowId}");
        }

        private void ShowSheet()
        {
            var mainWindow = _window ?? throw new InvalidOperationException("The provider main window is unavailable.");
            if (_sheet is null)
            {
                var sheet = new NSWindow(
                    new CGRect(0, 0, 360, 140),
                    NSWindowStyle.Titled | NSWindowStyle.Closable,
                    NSBackingStore.Buffered,
                    false)
                {
                    Title = "Everywhere AX Probe Sheet",
                    AccessibilityIdentifier = "probe-sheet",
                };
                var input = new NSTextField(new CGRect(24, 56, 312, 28))
                {
                    StringValue = "sheet-value",
                };
                ((NSView)input).AccessibilityIdentifier = "probe-sheet-input";
                sheet.ContentView?.AddSubview(input);
                _sheet = sheet;
                _sheetInput = input;
                mainWindow.BeginSheet(sheet, _ => { });
            }

            ActivateApplication();
            var requiredSheet = _sheet ?? throw new InvalidOperationException("The sheet probe window is unavailable.");
            requiredSheet.MakeKeyWindow();
            if (!requiredSheet.MakeFirstResponder(_sheetInput))
            {
                throw new InvalidOperationException("AppKit refused to focus the sheet probe element.");
            }

            WriteStatus($"SHEET\t{GetWindowId(mainWindow)}\t{GetWindowId(_sheet)}");
        }

        private void PrepareContent()
        {
            if (_contentWindow is null)
            {
                var window = CreateWindow("Everywhere AX Probe Content", "probe-content-window", new CGRect(120, 120, 680, 540));
                var largeText = CreateLargeText();
                var textView = new NSTextView(new CGRect(20, 350, 640, 160))
                {
                    Value = largeText,
                    Editable = true,
                };
                ((NSView)textView).AccessibilityIdentifier = "probe-large-text";
                var collectionGroup = new NSView(new CGRect(20, 20, 640, 300))
                {
                    AccessibilityElement = true,
                    AccessibilityIdentifier = "probe-collection",
                    AccessibilityRole = NSAccessibilityRoles.GroupRole,
                };
                using var collectionValue = new NSString("collection-fallback-text");
                collectionGroup.AccessibilityValue = collectionValue;

                for (var index = 0; index < CollectionItemCount; index++)
                {
                    var button = CreateCollectionButton(index);
                    collectionGroup.AddSubview(button);
                    _collectionButtons.Add(button);
                }

                window.ContentView?.AddSubview(textView);
                window.ContentView?.AddSubview(collectionGroup);
                window.MakeKeyAndOrderFront(null);
                window.MakeFirstResponder(textView);
                _contentWindow = window;
                _largeTextView = textView;
                _collectionGroup = collectionGroup;
            }

            WriteStatus($"CONTENT\t{GetWindowId(_contentWindow)}\t{LargeTextLength}\t{_collectionButtons.Count}");
        }

        private void FocusLargeText()
        {
            var window = _contentWindow ?? throw new InvalidOperationException("The content probe window is unavailable.");
            var textView = _largeTextView ?? throw new InvalidOperationException("The large-text probe element is unavailable.");
            window.MakeKeyAndOrderFront(null);
            if (!window.MakeFirstResponder(textView))
            {
                throw new InvalidOperationException("AppKit refused to focus the large-text probe element.");
            }

            WriteStatus($"FOCUSED_TEXT\t{LargeTextLength}");
        }

        private void FocusCollection()
        {
            var window = _contentWindow ?? throw new InvalidOperationException("The content probe window is unavailable.");
            if (_collectionButtons.Count == 0)
            {
                throw new InvalidOperationException("The collection probe elements are unavailable.");
            }

            var button = _collectionButtons[_collectionButtons.Count / 2];
            window.MakeKeyAndOrderFront(null);
            if (!window.MakeFirstResponder(button))
            {
                throw new InvalidOperationException("AppKit refused to focus the collection probe element.");
            }

            WriteStatus($"FOCUSED_COLLECTION\t{button.Title}");
        }

        private void MutateCollection()
        {
            var group = _collectionGroup ?? throw new InvalidOperationException("The collection probe group is unavailable.");
            const int mutationCount = 16;
            for (var index = 0; index < mutationCount; index++)
            {
                var removed = _collectionButtons[0];
                _collectionButtons.RemoveAt(0);
                removed.RemoveFromSuperview();
                removed.Dispose();
            }

            var firstNewIndex = CollectionItemCount;
            for (var index = 0; index < mutationCount; index++)
            {
                var button = CreateCollectionButton(firstNewIndex + index);
                group.AddSubview(button);
                _collectionButtons.Add(button);
            }

            WriteStatus($"MUTATED_COLLECTION\t{mutationCount}\t{_collectionButtons.Count}");
        }

        private void CloseContent()
        {
            var windowId = _contentWindow is null ? 0 : GetWindowId(_contentWindow);
            CloseWindow(ref _contentWindow);
            _largeTextView = null;
            _collectionGroup = null;
            _collectionButtons.Clear();
            WriteStatus($"CLOSED_CONTENT\t{windowId}");
        }

        private void PrepareCapture(bool isBorderless)
        {
            if (_captureWindow is null || _isCaptureWindowBorderless != isBorderless)
            {
                CloseWindow(ref _captureOccluderWindow);
                CloseWindow(ref _captureWindow);
                _captureQuadrants.Clear();
                var style = isBorderless ? NSWindowStyle.Borderless :
                    NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable;
                var window = new CaptureProbeWindow(new CGRect(160, 160, 400, 320), style)
                {
                    Title = "Everywhere AX Capture Probe",
                    AccessibilityIdentifier = isBorderless ? "probe-capture-borderless-window" : "probe-capture-window",
                    AccessibilityElement = true,
                };
                var content = window.ContentView ?? throw new InvalidOperationException("The capture probe window has no content view.");
                var width = content.Bounds.Width / 2;
                var height = content.Bounds.Height / 2;
                AddCaptureQuadrant(content, new CGRect(0, height, width, height), NSColor.Red, "capture-top-left");
                AddCaptureQuadrant(content, new CGRect(width, height, width, height), NSColor.Green, "capture-top-right");
                AddCaptureQuadrant(content, new CGRect(0, 0, width, height), NSColor.Blue, "capture-bottom-left");
                AddCaptureQuadrant(content, new CGRect(width, 0, width, height), NSColor.Yellow, "capture-bottom-right");
                _captureWindow = window;
                _captureInitialFrame = window.Frame;
                _isCaptureWindowBorderless = isBorderless;
            }

            var requiredWindow = _captureWindow;
            requiredWindow.MakeKeyAndOrderFront(null);
            ActivateApplication();
            _captureBackingScaleFactor = (double)(requiredWindow.Screen?.BackingScaleFactor ?? NSScreen.Screens[0].BackingScaleFactor);
            _capturePlacement = "onscreen";
            _isCaptureOccluded = false;

            WriteCaptureStatus("CAPTURE");
        }

        private void MinimizeCapture()
        {
            var window = _captureWindow ?? throw new InvalidOperationException("Prepare the capture window before minimizing it.");
            window.Miniaturize(null);
            WriteCaptureStatus("MINIMIZED_CAPTURE");
        }

        private void RestoreCapture()
        {
            var window = _captureWindow ?? throw new InvalidOperationException("Prepare the capture window before restoring it.");
            window.Deminiaturize(null);
            window.MakeKeyAndOrderFront(null);
            WriteCaptureStatus("RESTORED_CAPTURE");
        }

        private void OccludeCapture()
        {
            var window = _captureWindow ?? throw new InvalidOperationException("Prepare the capture window before occluding it.");
            CloseWindow(ref _captureOccluderWindow);
            var occluder = new NSWindow(window.Frame, NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
            {
                BackgroundColor = NSColor.Magenta,
                HasShadow = false,
                IgnoresMouseEvents = true,
            };
            occluder.OrderFrontRegardless();
            _captureOccluderWindow = occluder;
            _isCaptureOccluded = true;
            WriteCaptureStatus("OCCLUDED_CAPTURE");
        }

        private void RevealCapture()
        {
            CloseWindow(ref _captureOccluderWindow);
            _isCaptureOccluded = false;
            WriteCaptureStatus("REVEALED_CAPTURE");
        }

        private void MoveCaptureOffscreen(bool isFullyOffscreen)
        {
            var window = _captureWindow ?? throw new InvalidOperationException("Prepare the capture window before moving it offscreen.");
            CloseWindow(ref _captureOccluderWindow);
            _isCaptureOccluded = false;
            var frame = window.Frame;
            var desktopLeft = NSScreen.Screens.Min(static screen => screen.Frame.X);
            var x = isFullyOffscreen ? desktopLeft - frame.Width - 80 : desktopLeft - frame.Width / 2;
            window.SetFrameOrigin(new CGPoint(x, frame.Y));
            _capturePlacement = isFullyOffscreen ? "fully-offscreen" : "partially-offscreen";
            WriteCaptureStatus(isFullyOffscreen ? "FULLY_OFFSCREEN_CAPTURE" : "PARTIALLY_OFFSCREEN_CAPTURE");
        }

        private void RestoreCapturePosition()
        {
            var window = _captureWindow ?? throw new InvalidOperationException("Prepare the capture window before restoring its position.");
            CloseWindow(ref _captureOccluderWindow);
            _isCaptureOccluded = false;
            window.SetFrame(_captureInitialFrame, true);
            window.MakeKeyAndOrderFront(null);
            _capturePlacement = "onscreen";
            WriteCaptureStatus("RESTORED_CAPTURE_POSITION");
        }

        private void WriteCaptureStatus(string status)
        {
            var window = _captureWindow ?? throw new InvalidOperationException("The capture probe window is unavailable.");
            var primaryHeight = NSScreen.Screens[0].Frame.Height;
            var points = _captureQuadrants.Select(view =>
            {
                var windowRect = view.ConvertRectToView(view.Bounds, null);
                var screenRect = window.ConvertRectToScreen(windowRect);
                return new CaptureProbePoint(
                    view.AccessibilityIdentifier ?? throw new InvalidOperationException("A capture quadrant has no accessibility identifier."),
                    (double)(screenRect.X + screenRect.Width / 2),
                    (double)(primaryHeight - screenRect.Y - screenRect.Height / 2));
            }).ToArray();
            var observation = new CaptureProviderObservation(
                GetWindowId(window),
                _captureBackingScaleFactor,
                _isCaptureWindowBorderless,
                window.IsMiniaturized,
                _capturePlacement,
                CalculateVisibleAreaRatio(window.Frame),
                _isCaptureOccluded,
                _captureOccluderWindow is null ? null : GetWindowId(_captureOccluderWindow),
                points);
            WriteStatus(status + "\t" + System.Text.Json.JsonSerializer.Serialize(observation));
        }

        private static double CalculateVisibleAreaRatio(CGRect windowFrame)
        {
            var visibleArea = 0d;
            foreach (var screen in NSScreen.Screens)
            {
                var screenFrame = screen.Frame;
                var width = Math.Max(0, Math.Min(windowFrame.X + windowFrame.Width, screenFrame.X + screenFrame.Width) - Math.Max(windowFrame.X, screenFrame.X));
                var height = Math.Max(0, Math.Min(windowFrame.Y + windowFrame.Height, screenFrame.Y + screenFrame.Height) - Math.Max(windowFrame.Y, screenFrame.Y));
                visibleArea += width * height;
            }

            return Math.Min(1, visibleArea / (windowFrame.Width * windowFrame.Height));
        }

        private void AddCaptureQuadrant(NSView parent, CGRect frame, NSColor color, string identifier)
        {
            var view = new CaptureColorView(frame, color)
            {
                AccessibilityElement = true,
                AccessibilityIdentifier = identifier,
                AccessibilityRole = NSAccessibilityRoles.GroupRole,
            };
            parent.AddSubview(view);
            _captureQuadrants.Add(view);
        }

        private void QueueProviderAction(Action action)
        {
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    WriteStatus($"ERROR\t{exception.GetType().Name}\t{exception.Message}");
                }
            });
        }

        private static NSWindow CreateWindow(string title, string identifier, CGRect frame) => new(
            frame,
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered,
            false)
        {
            Title = title,
            AccessibilityIdentifier = identifier,
        };

        private static NSButton CreateCollectionButton(int index)
        {
            var button = new NSButton(new CGRect((index % 10) * 62, (index / 10) * 22, 58, 20))
            {
                Title = $"Item {index:D3}",
            };
            ((NSView)button).AccessibilityIdentifier = $"probe-item-{index:D3}";
            return button;
        }

        private static string CreateLargeText()
        {
            const string seed = "Everywhere-AX-range-中文-🙂-";
            return string.Create(
                LargeTextLength,
                seed,
                static (characters, value) =>
                {
                    for (var index = 0; index < characters.Length; index++)
                    {
                        characters[index] = value[index % value.Length];
                    }
                });
        }

        private static uint GetWindowId(NSWindow window) => checked((uint)window.WindowNumber);

        private static void ActivateApplication()
        {
            if (OperatingSystem.IsMacOSVersionAtLeast(14))
            {
                NSApplication.SharedApplication.Activate();
                return;
            }

            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        }

        private static void CloseWindow<TWindow>(ref TWindow? window) where TWindow : NSWindow
        {
            window?.Close();
            window?.Dispose();
            window = null;
        }

        private sealed class CaptureColorView(CGRect frame, NSColor color) : NSView(frame)
        {
            public override void DrawRect(CGRect dirtyRect)
            {
                color.SetFill();
                NSBezierPath.FillRect(Bounds);
            }
        }

        private sealed class CaptureProbeWindow(CGRect frame, NSWindowStyle style) : NSWindow(frame, style, NSBackingStore.Buffered, false)
        {
            // AppKit normally keeps part of a titled window visible. The probe must be able to create a truly
            // off-desktop surface so that capture capability is not confused with AppKit placement policy.
            public override CGRect ConstrainFrameRect(CGRect frameRect, NSScreen? screen) => frameRect;
        }

        private sealed record CaptureProbePoint(string Name, double X, double Y);

        private sealed record CaptureProviderObservation(
            uint WindowId,
            double BackingScaleFactor,
            bool IsBorderless,
            bool IsMinimized,
            string Placement,
            double VisibleAreaRatio,
            bool IsOccluded,
            uint? OccluderWindowId,
            IReadOnlyList<CaptureProbePoint> Points);

        private void WriteStatus(string status)
        {
            lock (_outputGate)
            {
                Console.WriteLine(status);
                Console.Out.Flush();
            }
        }
    }
}
