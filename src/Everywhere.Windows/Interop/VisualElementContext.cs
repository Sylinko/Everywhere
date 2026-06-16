using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform;
using Everywhere.Common;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.Windows.Extensions;
using Interop.UIAutomationClient;
using Serilog;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Vector = Avalonia.Vector;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext(IWindowHelper windowHelper) : IVisualElementContext
{
    private static readonly IUIAutomation Automation = new CUIAutomation8Class();
    private static readonly IUIAutomationTreeWalker TreeWalker = Automation.ContentViewWalker;

    public IVisualElement? FocusedElement => TryCreateVisualElement(Automation.GetFocusedElement);

    public IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        switch (mode)
        {
            case ScreenSelectionMode.Element:
            {
                return TryCreateVisualElement(() => Automation.ElementFromPoint(new tagPOINT { x = point.X, y = point.Y }));
            }
            case ScreenSelectionMode.Window:
            {
                IVisualElement? element = TryCreateVisualElement(() => Automation.ElementFromPoint(new tagPOINT { x = point.X, y = point.Y }));
                while (element is AutomationVisualElementImpl { IsTopLevelWindow: false })
                {
                    element = element.Parent;
                }

                return element;
            }
            case ScreenSelectionMode.Screen:
            {
                var hMonitor = PInvoke.MonitorFromPoint(new Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                return hMonitor == HMONITOR.Null ? null : new ScreenVisualElementImpl(hMonitor);
            }
        }

        return null;
    }

    public IVisualElement? ElementFromPointer(ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        return !PInvoke.GetCursorPos(out var point) ? null : ElementFromPoint(new PixelPoint(point.X, point.Y), mode);
    }

    public IVisualElement? ElementFromWindowHandle(IntPtr windowHandle)
    {
        return TryCreateVisualElement(() => Automation.ElementFromHandle(windowHandle));
    }

    public Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionMode? initialMode) => PickerSession.PickAsync(windowHelper, initialMode);

    public Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode) => ScreenshotSession.TakeAsync(windowHelper, initialMode);

    private static AutomationVisualElementImpl? TryCreateVisualElement(Func<IUIAutomationElement?> factory)
    {
        try
        {
            if (factory() is { } element) return new AutomationVisualElementImpl(element);
        }
        catch (Exception ex)
        {
            Log.ForContext<VisualElementContext>().Error(
                new HandledException(ex, new DirectLocaleKey("Failed to get AutomationElement")),
                "Failed to get AutomationElement");
        }

        return null;
    }

    /// <summary>
    /// Captures a screenshot of the specified rectangle on the screen.
    /// </summary>
    /// <param name="rect"></param>
    /// <returns></returns>
    private static Win32CapturedBitmapData? CaptureScreen(PixelRect rect)
    {
        var x = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var y = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var w = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        var h = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        var screenRect = new PixelRect(x, y, w, h);

        rect = rect.Intersect(screenRect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var gdiBitmap = new System.Drawing.Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(gdiBitmap))
        {
            graphics.CopyFromScreen(rect.X, rect.Y, 0, 0, new Size(rect.Width, rect.Height));
        }

        return new Win32CapturedBitmapData(gdiBitmap, new Vector(96, 96));
    }

    /// <summary>
    /// Sends the specified keyboard shortcut via win32 SendInput api
    /// </summary>
    /// <param name="shortcut"></param>
    /// <exception cref="Win32Exception"></exception>
    private static void SendInput(KeyboardShortcut shortcut)
    {
        // Use PInvoke.SendInput to send the shortcut to the focused element.
        var inputs = new List<INPUT>();
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control)) MakeInputs(VIRTUAL_KEY.VK_CONTROL);
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt)) MakeInputs(VIRTUAL_KEY.VK_MENU);
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift)) MakeInputs(VIRTUAL_KEY.VK_SHIFT);
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta)) MakeInputs(VIRTUAL_KEY.VK_LWIN);
        MakeInputs(shortcut.Key.ToVirtualKey());

        var result = PInvoke.SendInput(CollectionsMarshal.AsSpan(inputs), Unsafe.SizeOf<INPUT>());
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to send keyboard input to the target element.");
        }

        void MakeInputs(VIRTUAL_KEY vk)
        {
            inputs.InsertRange(
                inputs.Count / 2,
                [
                    new INPUT
                    {
                        type = INPUT_TYPE.INPUT_KEYBOARD,
                        Anonymous = new INPUT._Anonymous_e__Union
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = vk,
                                dwFlags = 0,
                            }
                        }
                    },
                    new INPUT
                    {
                        type = INPUT_TYPE.INPUT_KEYBOARD,
                        Anonymous = new INPUT._Anonymous_e__Union
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = vk,
                                dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                            }
                        }
                    },
                ]);
        }
    }

    /// <summary>
    /// A disposable wrapper that holds a System.Drawing.Bitmap and its locked BitmapData.
    /// Exposes the raw memory pointer to be consumed by other rendering engines (like Avalonia or Skia).
    /// </summary>
    private sealed class Win32CapturedBitmapData : IVisualElement.ICapturedBitmapData
    {
        public Avalonia.Platform.PixelFormat Format { get; }
        public AlphaFormat AlphaFormat { get; }
        public nint Data => _bitmapData?.Scan0 ?? IntPtr.Zero;
        public PixelSize Size { get; }
        public Vector Dpi { get; }
        public int Stride => _bitmapData?.Stride ?? 0;

        private readonly System.Drawing.Bitmap _gdiBitmap;
        private BitmapData? _bitmapData;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the pointer, taking ownership of the provided GDI+ bitmap.
        /// </summary>
        public Win32CapturedBitmapData(System.Drawing.Bitmap gdiBitmap, Vector dpi)
        {
            _gdiBitmap = gdiBitmap;
            _bitmapData = _gdiBitmap.LockBits(
                new Rectangle(0, 0, _gdiBitmap.Width, _gdiBitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            Size = new PixelSize(_gdiBitmap.Width, _gdiBitmap.Height);
            Dpi = dpi;

            // Format32bppArgb maps directly to Bgra8888 on little-endian Windows
            Format = Avalonia.Platform.PixelFormat.Bgra8888;
            AlphaFormat = AlphaFormat.Opaque;
        }

        ~Win32CapturedBitmapData()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (_bitmapData != null)
            {
                _gdiBitmap.UnlockBits(_bitmapData);
                _bitmapData = null;
            }

            if (disposing)
            {
                _gdiBitmap.Dispose();
            }

            _disposed = true;
        }
    }
}