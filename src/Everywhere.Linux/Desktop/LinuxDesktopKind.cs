namespace Everywhere.Linux.Desktop;

/// <summary>
/// The explicitly selected desktop integration, independent of Avalonia's renderer.
/// </summary>
public enum LinuxDesktopKind
{
    /// <summary>
    /// The Hyprland compositor integration.
    /// </summary>
    Hyprland,

    /// <summary>
    /// The X11 desktop integration.
    /// </summary>
    X11,

    /// <summary>
    /// The Plasma Wayland desktop integration.
    /// </summary>
    PlasmaWayland,

    /// <summary>
    /// The GNOME Wayland desktop integration.
    /// </summary>
    GnomeWayland,

    /// <summary>
    /// The Niri Wayland compositor integration.
    /// </summary>
    NiriWayland,
}