using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

partial class VisualElementContext
{
    private class PickerSession : ScreenSelectionSession
    {
        private static ScreenSelectionMode _previousMode = ScreenSelectionMode.Element;

        public static async Task<IVisualElement?> PickAsync(IWindowHelper windowHelper, ScreenSelectionMode? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new PickerSession(windowHelper, initialMode ?? _previousMode);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<IVisualElement?> _pickingPromise = new();

        private PickerSession(IWindowHelper windowHelper, ScreenSelectionMode screenSelectionMode)
            : base(
                windowHelper,
                [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
                screenSelectionMode)
        {
        }

        protected override void OnClosed(EventArgs e)
        {
            _previousMode = CurrentMode;
            _pickingPromise.TrySetResult(SelectedElement);
            base.OnClosed(e);
        }
    }
}