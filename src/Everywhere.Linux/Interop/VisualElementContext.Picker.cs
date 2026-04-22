using Avalonia;
using Avalonia.Threading;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;

public partial class VisualElementContext
{
    private class ElementPicker : ScreenSelectionSession
    {
        private static ScreenSelectionModes _previousModes = ScreenSelectionModes.Window;
        public static async Task<IVisualElement?> PickAsync(
            VisualElementContext context,
            IWindowBackend backend,
            ScreenSelectionModes? initialMode)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var window = new ElementPicker(context, backend, initialMode ?? _previousModes);
            window.Show();
            window.Activate();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<IVisualElement?> _pickingPromise = new();
        private readonly VisualElementContext _context;
        private IVisualElement? _selectedElement;

        private ElementPicker(
            VisualElementContext context,
            IWindowBackend backend,
            ScreenSelectionModes screenSelectionMode)
            : base(backend, ScreenSelectionModes.Screen | ScreenSelectionModes.Window | ScreenSelectionModes.Element, screenSelectionMode)
        {
            _context = context;
            backend.SetFocusable(this, true);
        }

        protected override void OnCanceled()
        {
            _selectedElement = null;
        }

        protected override void OnCloseCleanup()
        {
            _pickingPromise.TrySetResult(_selectedElement);
        }

        protected override bool OnLeftButtonUp()
        {
            Backend.SetCloaked(ToolTipWindow, true);
            foreach (var maskWindow in MaskWindows) Backend.SetCloaked(maskWindow, true);
            return true;
        }

        protected override void OnMove(PixelPoint point)
        {
            _selectedElement = _context.ElementFromPoint(point, CurrentMode);

            var maskRect = new PixelRect();
            if (_selectedElement != null)
            {
                maskRect = _selectedElement.BoundingRectangle;
            }

            // Safety check for invalid rects
            if (maskRect.Width < 0 || maskRect.Height < 0)
            {
                maskRect = new PixelRect();
            }

            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(maskRect);
            ToolTipWindow.ToolTip.Element = _selectedElement;
        }
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _previousModes = CurrentMode;
        }
    }
}