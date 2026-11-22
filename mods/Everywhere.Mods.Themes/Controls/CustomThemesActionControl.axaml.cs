using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Everywhere.Common;
using Everywhere.Extensions;

namespace Everywhere.Mods.Themes.Controls;

/// <summary>
/// Provides quick actions to open the custom JSON themes directory and reload theme definitions.
/// </summary>
public sealed partial class CustomThemesActionControl : UserControl
{
    private readonly string _customThemesDirectory;
    private readonly Action _reloadThemes;

    /// <summary>
    /// Initializes a new <see cref="CustomThemesActionControl"/> instance.
    /// </summary>
    public CustomThemesActionControl() : this(string.Empty, static () => { }) { }

    /// <summary>
    /// Initializes a new <see cref="CustomThemesActionControl"/> bound to a target directory and reload callback.
    /// </summary>
    public CustomThemesActionControl(string customThemesDirectory, Action reloadThemes)
    {
        _customThemesDirectory = customThemesDirectory;
        _reloadThemes = reloadThemes;
        AvaloniaXamlLoader.Load(this);
    }

    private void HandleOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_customThemesDirectory))
        {
            return;
        }

        Directory.CreateDirectory(_customThemesDirectory);
        App.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_customThemesDirectory))
            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private void HandleReloadClick(object? sender, RoutedEventArgs e)
    {
        _reloadThemes();
    }
}
