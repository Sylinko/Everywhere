# X11 desktop starter

This restores core designs from `fork/dev-linux` through the current Linux desktop
contracts, targeting a minimal Plasma/KWin X11 session. It does not restore the
old branch's complete facade, shortcut hooks, selection listener or toolbar.

`X11CompositorStateProvider` reads EWMH stacking, foreground, desktop visibility,
PID, client geometry and optional frame extents. `X11SilentScreenCapturer` reads
visible root pixels. Short-lived XCB connections own every reply and native error;
foreign-window races do not install a global Xlib error handler in Avalonia.
`X11WindowController` uses Avalonia's ordinary X11 windows, WM_HINTS and input
shapes. The shared AT-SPI and picker/capture pipeline remain available.

The first target is one X screen at scale 1 and a standard little-endian 32-bit
TrueColor pixel layout. RandR output enumeration, mixed DPI and text-selection
assistance remain unported. Global keyboard shortcuts use the user-approved
GlobalShortcuts portal when the desktop provides it; global mouse shortcuts remain
unsupported. Focused shortcut recording uses the common `LinuxShortcutListener`
implementation and `IKeyboardShortcutScope`, while the authenticated Input Host
remains healthy and idle.

An explicit X11 session takes precedence over inherited Wayland/Hyprland markers.
Main selects Avalonia X11 directly rather than falling back from Wayland.

Validate ordinary Main/Chat windows, EWMH foreground/stacking, client/frame
geometry, visible root-pixel capture and ordinary input in a disposable profile.
Follow the [Linux acceptance checklist](LinuxHyprlandValidation.md). The supported
single-screen scale/pixel-layout boundary must be retained when reporting results.