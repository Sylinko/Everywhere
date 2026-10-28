# Ordinary Wayland desktop starter

Plasma, GNOME and Niri Wayland start through Avalonia.Wayland's ordinary toplevel and text-input
implementation. It does not install `HyprlandWindowingPlatform`, issue compositor
commands, assign guessed global coordinates or reuse Hyprland's input bindings.
`StandardWaylandWindowController` permits ordinary floating-application intent
while explicitly rejecting overlay roles and unsupported global window policies.

`UnavailableWaylandStateProvider`, `UnavailableWaylandScreenCapturer` and
`UnavailableWaylandSelectionService` mark global observation, silent capture and
interactive picking as unported. The selection service rejects requests before
creating a surface. Text-selection assistance remains unported. Plasma/GNOME
(and portal-capable X11 desktops) register global keyboard shortcuts through the
user-approved XDG GlobalShortcuts portal. Niri still requires a compositor binding
to the application command; recording a chord does not create that binding.
Focused shortcut recording uses `LinuxShortcutListener` and the shared
`IKeyboardShortcutScope` contract; it requires no compositor-specific keyboard
hook. Portal approval is managed separately from the Input Host so user denial
does not repeatedly restart a healthy Host.

This is a starting point for desktop contributors, not feature parity with
Hyprland. Future desktop work should replace the unavailable capabilities at the
composition root without moving desktop policies into shared Core or changing
Windows/macOS behavior. Ordinary input uses the framework's native protocols;
no separate input-method implementation or dependency is introduced here.

## Validation

In a disposable Plasma, GNOME or Niri session, launch actual Main/Chat with the session's
native input method. Check preedit, candidate placement, Unicode commits and focus
transitions. Both authenticated Hosts should remain healthy without recovery loops.
Global observation, overlays and silent capture must report their unavailable
capabilities rather than borrowing Hyprland's session services. Validate global
shortcut activation separately from focused recording, including native portal
approval, recording an existing chord and activation from another application.

Use the [Linux acceptance checklist](LinuxHyprlandValidation.md) for shared setup
and cleanup. Ordinary window/input checks do not establish full desktop parity,
distribution coverage or multi-monitor acceptance.

## Main and Chat rendering

StandardDesktopWindowAppearance supplies opaque theme-aware backgrounds and
ordinary Main controls on Plasma/GNOME/Niri. The shared appearance also applies
to X11, which retains native window-manager decoration negotiation. Main and Chat
request native resizing from their edges. Chat retains its own title bar and accepts compositor-driven
sizes in both tiled and floating layouts, including after a minimum-size reset.
The ShadUI theme's unpainted shadow reserve is disabled so it does not leave a
transparent band between the client and frame. The complete drawn decoration
underlay is painted to avoid a remaining transparent KWin border seam. Standard
Wayland uses drawn decorations consistently across Hide/Show to avoid Avalonia 12.1.3 renegotiating a second KWin title bar.
See [the local desktop validation](LinuxDesktopWaylandValidation.md) for the
reproductions, experimental-option rationale, test failures and acceptance limits.

See [desktop shortcut validation](LinuxDesktopShortcutValidation.md) for the
portal lifecycle, stable Linux display identity, actual shortcut checks and the
Niri configuration boundary.