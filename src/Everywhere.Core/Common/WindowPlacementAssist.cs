using Avalonia.Controls;
using Everywhere.Configuration;

namespace Everywhere.Common;

/// <summary>
/// Provides methods to assist with saving and restoring window placement (position, size, and state) for Avalonia windows.
/// </summary>
public static class WindowPlacementAssist
{
    /// <summary>
    /// Attaches window placement saving and restoring behavior to the specified window.
    /// </summary>
    /// <param name="window"></param>
    /// <param name="keyValueStorage"></param>
    /// <param name="key"></param>
    /// <param name="safetyPadding"></param>
    /// <exception cref="ArgumentException"></exception>
    public static void Attach(Window window, IKeyValueStorage keyValueStorage, string key, double safetyPadding = 20d)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var storageKey = $"WindowPlacement.{key}";

        if (window.IsInitialized)
        {
            RestoreWindowPlacement();
            window.PositionChanged += HandleWindowPositionChanged;
            window.Resized += HandleWindowResized;
            window.Closed += HandleWindowClosed;
        }
        else
        {
            window.Initialized += HandleWindowInitialized;
        }

        void HandleWindowInitialized(object? o, EventArgs e)
        {
            window.Initialized -= HandleWindowInitialized;

            RestoreWindowPlacement();
            window.PositionChanged += HandleWindowPositionChanged;
            window.Resized += HandleWindowResized;
            window.Closed += HandleWindowClosed;
        }

        void HandleWindowPositionChanged(object? o, PixelPointEventArgs e)
        {
            SaveWindowPlacement();
        }

        void HandleWindowResized(object? o, WindowResizedEventArgs e)
        {
            SaveWindowPlacement();
        }

        void HandleWindowClosed(object? o, EventArgs e)
        {
            window.Closed -= HandleWindowClosed;
            window.Resized -= HandleWindowResized;
            window.PositionChanged -= HandleWindowPositionChanged;
        }

        void RestoreWindowPlacement()
        {
            if (keyValueStorage.Get<WindowPlacement?>(storageKey) is not { } placement) return;

            double x, y;
            if (window.Screens.All.Count == 0)
            {
                x = placement.X;
                y = placement.Y;
            }
            else
            {
                // Calculate scaling based on the target screen
                var targetScreen = window.Screens.ScreenFromPoint(placement.Position)
                    ?? window.Screens.Primary
                    ?? window.Screens.All.FirstOrDefault();
                var scaling = targetScreen?.Scaling ?? 1.0;

                var screenBounds = targetScreen?.WorkingArea ?? default;
                var actualWidth = placement.Width <= 0 ? 200d : placement.Width * scaling;
                var actualHeight = placement.Height <= 0 ? 200d : placement.Height * scaling;
                safetyPadding = Math.Max(0, safetyPadding);

                x = Math.Clamp(
                    placement.X,
                    screenBounds.X + safetyPadding,
                    Math.Max(screenBounds.X + safetyPadding, screenBounds.Right - actualWidth - safetyPadding));

                y = Math.Clamp(
                    placement.Y,
                    screenBounds.Y + safetyPadding,
                    Math.Max(screenBounds.Y + safetyPadding, screenBounds.Bottom - actualHeight - safetyPadding));
            }

            // Restore bounds first so that maximizing works correctly from the restored position
            window.Position = new PixelPoint((int)x, (int)y);

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SizeToContent = (placement.Width, placement.Height) switch
            {
                (< 0, < 0) => SizeToContent.WidthAndHeight,
                (< 0, _) => SizeToContent.Height,
                (_, < 0) => SizeToContent.Width,
                _ => SizeToContent.Manual
            };

            if (placement.Width > 0) window.Width = placement.Width;
            if (placement.Height > 0) window.Height = placement.Height;

            window.WindowState = placement.WindowState;
        }

        void SaveWindowPlacement()
        {
            // Do not save placement if the window is minimized
            if (window.WindowState == WindowState.Minimized) return;

            // Only save size and position if the window is in Normal state
            if (window.WindowState == WindowState.Normal)
            {
                var (width, height) = window.SizeToContent switch
                {
                    SizeToContent.Width => (-1, (int)window.Height),
                    SizeToContent.Height => ((int)window.Width, -1),
                    SizeToContent.Manual => ((int)window.Width, (int)window.Height),
                    _ => (-1, -1)
                };

                var placement = new WindowPlacement(
                    window.Position,
                    width,
                    height,
                    window.WindowState);
                keyValueStorage.Set(storageKey, placement);
            }
            else
            {
                // If maximized/minimized, only update the state, preserving the last normal bounds
                var existing = keyValueStorage.Get<WindowPlacement?>(storageKey);
                if (!existing.HasValue) return;

                var placement = existing.Value;
                placement.WindowState = window.WindowState;
                keyValueStorage.Set(storageKey, placement);
            }
        }
    }
}