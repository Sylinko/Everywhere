using Avalonia.Threading;
using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

partial class VisualElementContext
{
    private class PickerSession : ScreenSelectionSession
    {
        private static ScreenSelectionModes _previousModes = ScreenSelectionModes.Element;

        public static async Task<IVisualElement?> PickAsync(IWindowHelper windowHelper, ScreenSelectionModes? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new PickerSession(windowHelper, initialMode ?? _previousModes);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<IVisualElement?> _pickingPromise = new();

        private PickerSession(IWindowHelper windowHelper, ScreenSelectionModes screenSelectionMode) :
            base(windowHelper, ScreenSelectionModes.Screen | ScreenSelectionModes.Window | ScreenSelectionModes.Element, screenSelectionMode)
        {
        }

        protected override void OnClosed(EventArgs e)
        {
            _previousModes = CurrentMode;
            _pickingPromise.TrySetResult(SelectedElement);
            base.OnClosed(e);
        }
    }
}