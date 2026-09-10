using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.Mac.Automation;

namespace Everywhere.Mac.Interop;

public sealed partial class MacScreenSelectionService
{
    private sealed class PickerSession : ScreenSelectionSession
    {
        private static ScreenSelectionMode _previousMode = ScreenSelectionMode.Element;

        public static async Task<VisualElementQueryResult?> PickAsync(
            IWindowHelper windowHelper,
            MacVisualElementBackend visualElementBackend,
            VisualElementRetention retention,
            ScreenSelectionMode? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new PickerSession(windowHelper, visualElementBackend, retention, initialMode ?? _previousMode);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<VisualElementQueryResult?> _pickingPromise = new();
        private readonly VisualElementRetention _destinationRetention;

        private PickerSession(
            IWindowHelper windowHelper,
            MacVisualElementBackend visualElementBackend,
            VisualElementRetention destinationRetention,
            ScreenSelectionMode screenSelectionMode)
            : base(
                windowHelper,
                visualElementBackend,
                destinationRetention.Context,
                [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
                screenSelectionMode)
        {
            _destinationRetention = destinationRetention;
        }

        protected override void OnClosed(EventArgs e)
        {
            _previousMode = CurrentMode;
            _pickingPromise.TrySetResult(RetainPickingElement(_destinationRetention));
            base.OnClosed(e);
        }
    }
}