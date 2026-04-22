using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Windows.Interop;

partial class VisualElementContext
{
    private sealed class MultiSelectionSessionWindow : ScreenSelectionSessionWindow
    {
        private static ScreenSelectionModes _previousMode = ScreenSelectionModes.Element;

        public static async Task<IReadOnlyList<IVisualElement>> SelectAsync(IWindowHelper windowHelper, ScreenSelectionModes? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new MultiSelectionSessionWindow(windowHelper, initialMode ?? _previousMode);
            window.Show();
            return await window._selectionPromise.Task;
        }

        private readonly List<IVisualElement> _selectedVisualElements = [];
        private readonly TaskCompletionSource<List<IVisualElement>> _selectionPromise = new();

        private Timer? _selectedElementTrackerTimer;

        private MultiSelectionSessionWindow(IWindowHelper windowHelper, ScreenSelectionModes initialMode)
            : base(windowHelper, ScreenSelectionModes.Screen | ScreenSelectionModes.Window | ScreenSelectionModes.Element, initialMode) { }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                _selectionPromise.TrySetResult(_selectedVisualElements);
            }

            base.OnPointerReleased(e);
        }

        protected override bool OnLeftButtonUp()
        {
            if (PickingElement is { } pickingElement)
            {
                Switch(pickingElement);
            }

            return false;
        }

        /// <summary>
        /// Adds or removes a visual element to the mask window.
        /// This is used for multi-selection mode, showing a highlight and tracks the visual element consistently until it is removed.
        /// </summary>
        /// <param name="visualElement"></param>
        private void Switch(IVisualElement visualElement)
        {
            lock (_selectedVisualElements)
            {
                if (_selectedVisualElements.Count == 0)
                {
                    _selectedElementTrackerTimer = new Timer(TrackSelectedElements, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }

                if (_selectedVisualElements.Remove(visualElement))
                {
                    if (_selectedVisualElements.Count == 0)
                    {
                        DisposeCollector.DisposeToDefault(ref _selectedElementTrackerTimer);
                    }
                }
                else
                {
                    // Not exist, just add
                    _selectedVisualElements.Add(visualElement);
                }
            }

            Task.Run(() =>
            {
                // Update highlights immediately after selection change
                TrackSelectedElements(null);
            });
        }

        private void TrackSelectedElements(object? state)
        {
            List<IVisualElement> visualElementsToProcess;
            lock (_selectedVisualElements)
            {
                visualElementsToProcess = _selectedVisualElements.ToList();
            }

            var highlightRects = new PixelRect[visualElementsToProcess.Count];
            for (var i = 0; i < visualElementsToProcess.Count; i++)
            {
                try
                {
                    highlightRects[i] = visualElementsToProcess[i].BoundingRectangle;
                }
                catch (Exception ex)
                {
                    Log.ForContext<MultiSelectionSessionWindow>().Error(ex, "Failed to get bounding rectangle of visual element");
                    highlightRects[i] = default;
                }
            }

            Dispatcher.UIThread.Invoke(() =>
            {
                foreach (var maskWindow in MaskWindows) maskWindow.SetHighlights(highlightRects);
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            DisposeCollector.DisposeToDefault(ref _selectedElementTrackerTimer);
            _previousMode = CurrentMode;
            _selectionPromise.TrySetResult([]);

            base.OnClosed(e);
        }
    }
}