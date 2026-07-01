using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Interop;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    public unsafe IEnumerable<IVisualElement> Screens
    {
        get
        {
            var monitors = new List<HMONITOR>();
            PInvoke.EnumDisplayMonitors(
                HDC.Null,
                null,
                (hMonitor, _, _, _) =>
                {
                    monitors.Add(hMonitor);
                    return true;
                },
                0);
            return monitors.Select(hMonitor => new ScreenVisualElementImpl(hMonitor));
        }
    }

    private unsafe class ScreenVisualElementImpl(HMONITOR hMonitor) : IVisualElement
    {
        public string Id => $"Screen:{_hMonitor}";

        public IVisualElement? Parent => null;

        /// <summary>
        /// Gets first window on the screen.
        /// </summary>
        public IEnumerable<IVisualElement> Children
        {
            get
            {
                List<IVisualElement> result = [];
                PInvoke.EnumWindows(
                    (hWnd, _) =>
                    {
                        if (PInvoke.GetAncestor(hWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != hWnd) return true; // ignore child windows
                        if (!PInvoke.IsWindowVisible(hWnd)) return true;

                        var windowPlacement = new WINDOWPLACEMENT();
                        if (!PInvoke.GetWindowPlacement(hWnd, ref windowPlacement) ||
                            windowPlacement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMINIMIZED) return true;

                        if (PInvoke.MonitorFromWindow(hWnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL) != _hMonitor) return true;

                        if (TryCreateVisualElement(() => Automation.ElementFromHandle(hWnd)) is not { } visualElement) return true;

                        result.Add(visualElement);
                        return true; // continue enumeration
                    },
                    0);
                return result;
            }
        }

        public VisualElementSiblingAccessor SiblingAccessor => new SiblingAccessorImpl(this);

        public VisualElementType Type => VisualElementType.Screen;

        public VisualElementStates States => VisualElementStates.None;

        public string? Name => null;

        public PixelRect BoundingRectangle
        {
            get
            {
                var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                return PInvoke.GetMonitorInfo(_hMonitor, ref mi) ?
                    new PixelRect(
                        mi.rcMonitor.X,
                        mi.rcMonitor.Y,
                        mi.rcMonitor.Width,
                        mi.rcMonitor.Height) :
                    default;
            }
        }

        public int ProcessId => -1;

        public nint NativeWindowHandle => -1;

        private readonly HMONITOR _hMonitor = hMonitor;

        public string? GetText(int maxLength = -1) => null;

        public void Invoke() => throw new InvalidOperationException("Screen is not invokable.");

        public void SetText(string text) => throw new InvalidOperationException("Cannot set text on screen.");

        public void SendShortcut(KeyboardShortcut shortcut) => SendInput(shortcut);

        public string? GetSelectionText() => null;

        public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
        {
            return CaptureScreen(BoundingRectangle) is not { } bitmap ?
                throw new InvalidOperationException("Failed to capture screen.") :
                Task.FromResult<IVisualElement.ICapturedBitmapData>(bitmap);
        }

        private sealed class SiblingAccessorImpl(ScreenVisualElementImpl visualElement) : VisualElementSiblingAccessor
        {
            private List<HMONITOR>? _monitors;
            private int _startingIndex;

            protected override void EnsureResources()
            {
                if (_monitors is not null) return;

                _monitors = [];
                PInvoke.EnumDisplayMonitors(
                    HDC.Null,
                    null,
                    (hMonitor, _, _, _) =>
                    {
                        _monitors.Add(hMonitor);
                        return true;
                    },
                    0);

                _startingIndex = _monitors.IndexOf(visualElement._hMonitor);
            }

            protected override void ReleaseResources() => _monitors = null;

            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                if (_monitors is not { } monitors) yield break;

                var currentIndex = _startingIndex;
                while (currentIndex < monitors.Count)
                {
                    yield return new ScreenVisualElementImpl(monitors[currentIndex]);
                    currentIndex++;
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                if (_monitors is not { } monitors) yield break;

                var currentIndex = _startingIndex;
                while (currentIndex >= 0)
                {
                    yield return new ScreenVisualElementImpl(monitors[currentIndex]);
                    currentIndex--;
                }
            }
        }
    }
}