using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Automation;

namespace Everywhere.Windows.Interop;

/// <summary>
/// A utility class for picking visual elements from the screen.
/// </summary>
public sealed partial class WindowsScreenSelectionService
{
    /// <summary>
    /// A window that allows the user to pick an element from the screen.
    /// </summary>
    private sealed class PickerSession : ScreenSelectionSession
    {
        private static ScreenSelectionMode _previousMode = ScreenSelectionMode.Element;

        public static async Task<VisualElementQueryResult?> PickAsync(
            IWindowHelper windowHelper,
            IVisualElementBackend visualElementBackend,
            VisualElementRetention retention,
            ScreenSelectionMode? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new PickerSession(windowHelper, visualElementBackend, retention, initialMode ?? _previousMode);
            window.Show();
            return await window._pickingPromise.Task;
        }

        /// <summary>
        /// A promise that resolves to the picked visual element.
        /// </summary>
        private readonly TaskCompletionSource<VisualElementQueryResult?> _pickingPromise = new();

        private readonly VisualElementRetention _destinationRetention;

        private PickerSession(
            IWindowHelper windowHelper,
            IVisualElementBackend visualElementBackend,
            VisualElementRetention destinationRetention,
            ScreenSelectionMode initialMode)
            : base(
                windowHelper,
                visualElementBackend,
                destinationRetention.Context,
                [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
                initialMode)
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