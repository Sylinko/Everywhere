using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Everywhere.Views;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Provides readable window backgrounds on ordinary Linux desktops where
/// the requested compositor blur is unavailable.
/// </summary>
internal static class StandardDesktopWindowAppearance
{
    private static readonly IMultiValueConverter OpaqueBackgroundConverter = new OpaqueColorConverter();
    private static readonly ConditionalWeakTable<Window, object> ConfiguredWindows = new();

    /// <summary>
    /// Installs opaque decorations without unused shadow space on standard Linux desktops.
    /// </summary>
    public static void InstallStyles()
    {
        var application = Application.Current ?? throw new InvalidOperationException("Application is not initialized.");
        application.Styles.Add(new StyleInclude(new Uri("avares://Everywhere/"))
        {
            Source = new Uri("avares://Everywhere/Desktop/StandardDesktopStyles.axaml"),
        });
    }

    /// <summary>
    /// Applies desktop-specific decorations and a theme-aware opaque background once.
    /// </summary>
    public static void Configure(Window window)
    {
        var backgroundResource = window switch
        {
            ChatWindow => "AcrylicWindowBackgroundColor",
            TransientWindow => "WindowBackgroundColor",
            _ => null,
        };
        if (backgroundResource is null || ConfiguredWindows.TryGetValue(window, out _))
        {
            return;
        }

        if (window is TransientWindow)
        {
            window.ExtendClientAreaToDecorationsHint = false;
            window.WindowDecorations = WindowDecorations.Full;
            FitInitialSizeToScreen(window);
        }
        else
        {
            // The chat already has its own title bar.
            window.ExtendClientAreaToDecorationsHint = true;
            window.WindowDecorations = WindowDecorations.None;
            window.BorderThickness = new Thickness(1);
        }

        // The removed shadow margin must not be the only usable resize target.
        // Both the framed Main and borderless Chat accept gestures on their edges.
        StandardDesktopWindowResize.Configure(window);

        // ShadWindow's theme colors include alpha, and its template does not
        // provide Avalonia's transparency fallback layer. Keep the theme color
        // while making the toplevel opaque; the binding follows theme changes.
        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        window.Bind(TemplatedControl.BackgroundProperty, new MultiBinding
        {
            Bindings = [new DynamicResourceExtension(backgroundResource)],
            Converter = OpaqueBackgroundConverter,
        });
        ConfiguredWindows.Add(window, new object());
    }

    private static void FitInitialSizeToScreen(Window window)
    {
        var screen = window.Screens.ScreenFromPoint(window.Position) ?? window.Screens.Primary;
        if (screen is null || screen.Scaling <= 0 || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0)
        {
            return;
        }

        // Leave room for compositor decorations and avoid covering the screen edges.
        var maxWidth = Math.Max(window.MinWidth, screen.WorkingArea.Width / screen.Scaling - 32);
        var maxHeight = Math.Max(window.MinHeight, screen.WorkingArea.Height / screen.Scaling - 80);
        if (double.IsFinite(window.Width))
        {
            window.Width = Math.Min(window.Width, maxWidth);
        }

        if (double.IsFinite(window.Height))
        {
            window.Height = Math.Min(window.Height, maxHeight);
        }
    }

    private sealed class OpaqueColorConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is not [Color color])
            {
                return AvaloniaProperty.UnsetValue;
            }

            return new SolidColorBrush(Color.FromArgb(byte.MaxValue, color.R, color.G, color.B));
        }
    }
}