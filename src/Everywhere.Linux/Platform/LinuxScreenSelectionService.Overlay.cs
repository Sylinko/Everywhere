using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Views;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Linux.Platform;

partial class LinuxScreenSelectionService
{
    private sealed class PickerOverlay : Window
    {
        private readonly OutputCoordinateMap _map;
        private readonly double _desktopScale;
        private readonly Canvas _canvas;
        private readonly Border[] _masks;
        private readonly Border _outline = new()
        {
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false
        };
        private readonly ScreenSelectionToolTip _tip;

        public PickerOverlay(
            INativeHelper nativeHelper,
            OutputCoordinateMap map,
            double desktopScale,
            ScreenSelectionMode[] modes,
            ScreenSelectionMode mode,
            bool isApplicationPicker,
            Bitmap? background)
        {
            _map = map;
            _desktopScale = desktopScale;
            WindowDecorations = WindowDecorations.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SizeToContent = SizeToContent.Manual;
            ShowInTaskbar = false;
            CanResize = false;
            CanMaximize = false;
            CanMinimize = false;
            Topmost = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            Background = background is null ? Brushes.Transparent : new ImageBrush(background)
            {
                Stretch = Stretch.Fill
            };
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Position = map.AvaloniaBounds.Position;
            Width = map.AvaloniaBounds.Width / desktopScale;
            Height = map.AvaloniaBounds.Height / desktopScale;
            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true
            };
            _masks = Enumerable.Range(0, 4).Select(_ => new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)),
                IsHitTestVisible = false
            }).ToArray();
            foreach (var mask in _masks)
            {
                _canvas.Children.Add(mask);
            }

            _canvas.Children.Add(_outline);
            _tip = new ScreenSelectionToolTip(nativeHelper, modes)
            {
                Mode = mode,
                IsApplicationWindowPicker = isApplicationPicker,
                IsHitTestVisible = false,
                MaxWidth = 480
            };
            _canvas.Children.Add(_tip);
            Content = _canvas;
            SetSelection(null);
        }

        public PixelPoint ToLogical(Point point)
        {
            return _map.ToLogical(new PixelPoint(
                _map.AvaloniaBounds.X + (int)Math.Round(point.X * _desktopScale),
                _map.AvaloniaBounds.Y + (int)Math.Round(point.Y * _desktopScale)));
        }

        public void SetMode(ScreenSelectionMode mode)
        {
            _tip.Mode = mode;
            _tip.SetObservation(null, null, null);
        }

        public void SetObservation(VisualPickerObservation observation)
        {
            _tip.SetObservation(observation.Snapshot, observation.FailureKind, observation.FailureMessage);
        }

        public void SetFailure(VisualElementQueryFailureKind kind) => _tip.SetObservation(null, kind, null);

        public void MoveToolTip(PixelPoint pointer)
        {
            _tip.IsVisible = _map.LogicalBounds.Contains(pointer);
            if (!_tip.IsVisible)
            {
                return;
            }

            var local = _map.ToAvalonia(new PixelRect(pointer, new PixelSize(1, 1))).Position - _map.AvaloniaBounds.Position;
            _tip.Measure(Size.Infinity);
            Canvas.SetLeft(_tip, Math.Clamp(local.X / _desktopScale + 20, 0, Math.Max(0, Width - _tip.DesiredSize.Width)));
            Canvas.SetTop(_tip, Math.Clamp(local.Y / _desktopScale + 20, 0, Math.Max(0, Height - _tip.DesiredSize.Height)));
        }

        public void SetSelection(PixelRect? bounds)
        {
            _tip.SizeInfo = bounds is { Width: > 0, Height: > 0 } size ? $"{size.Width} x {size.Height}" : null;
            var visible = bounds?.Intersect(_map.LogicalBounds);
            _outline.IsVisible = visible is { Width: > 0, Height: > 0 };
            if (visible is not { Width: > 0, Height: > 0 } selected)
            {
                Place(_masks[0], new Rect(0, 0, Width, Height));
                for (var i = 1; i < 4; i++)
                {
                    Place(_masks[i], default);
                }
                return;
            }

            var mapped = _map.ToAvalonia(selected);
            var rectangle = new Rect(
                (mapped.X - _map.AvaloniaBounds.X) / _desktopScale,
                (mapped.Y - _map.AvaloniaBounds.Y) / _desktopScale,
                mapped.Width / _desktopScale,
                mapped.Height / _desktopScale);
            Place(_outline, rectangle);
            Place(_masks[0], new Rect(0, 0, Width, Math.Max(0, rectangle.Top)));
            Place(_masks[1], new Rect(0, rectangle.Bottom, Width, Math.Max(0, Height - rectangle.Bottom)));
            Place(_masks[2], new Rect(0, rectangle.Top, Math.Max(0, rectangle.Left), rectangle.Height));
            Place(_masks[3], new Rect(rectangle.Right, rectangle.Top, Math.Max(0, Width - rectangle.Right), rectangle.Height));
        }

        private static void Place(Control control, Rect bounds)
        {
            Canvas.SetLeft(control, bounds.X);
            Canvas.SetTop(control, bounds.Y);
            control.Width = bounds.Width;
            control.Height = bounds.Height;
        }
    }
}