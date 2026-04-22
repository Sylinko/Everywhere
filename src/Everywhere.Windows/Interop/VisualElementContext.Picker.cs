using Avalonia.Threading;
using Everywhere.Interop;

namespace Everywhere.Windows.Interop;

/// <summary>
/// A utility class for picking visual elements from the screen.
/// </summary>
public partial class VisualElementContext
{
    /// <summary>
    /// A window that allows the user to pick an element from the screen.
    /// </summary>
    private sealed class PickerSessionWindow : ScreenSelectionSessionWindow
    {
        private static ScreenSelectionModes _previousModes = ScreenSelectionModes.Element;

        public static async Task<IVisualElement?> PickAsync(IWindowHelper windowHelper, ScreenSelectionModes? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new PickerSessionWindow(windowHelper, initialMode ?? _previousModes);
            window.Show();
            return await window._pickingPromise.Task;
        }

        /// <summary>
        /// A promise that resolves to the picked visual element.
        /// </summary>
        private readonly TaskCompletionSource<IVisualElement?> _pickingPromise = new();

        private PickerSessionWindow(IWindowHelper windowHelper, ScreenSelectionModes initialMode)
            : base(windowHelper, ScreenSelectionModes.Screen | ScreenSelectionModes.Window | ScreenSelectionModes.Element, initialMode)
        {
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _previousModes = CurrentMode;
            _pickingPromise.TrySetResult(PickingElement);
        }
    }
}