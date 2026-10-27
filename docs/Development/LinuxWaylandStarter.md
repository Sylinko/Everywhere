# Ordinary Wayland desktop starter

Plasma and GNOME Wayland start through Avalonia.Wayland's ordinary toplevel and text-input
implementation. It does not install `HyprlandWindowingPlatform`, issue compositor
commands, assign guessed global coordinates or reuse Hyprland's input bindings.
`StandardWaylandWindowController` permits ordinary floating-application intent
while explicitly rejecting overlay roles and unsupported global window policies.

`UnavailableWaylandStateProvider`, `UnavailableWaylandScreenCapturer` and
`UnavailableWaylandSelectionService` mark global observation, silent capture and
interactive picking as unported. The selection service rejects requests before
creating a surface. Global shortcut registration and text-selection assistance
remain unported. Focused shortcut recording uses `LinuxShortcutListener` and the
shared `IKeyboardShortcutScope` contract; it requires no compositor-specific
keyboard hook. Unsupported global registrations fail locally rather than
repeatedly restarting an unsupported Input Host.

This is a starting point for desktop contributors, not feature parity with
Hyprland. Future desktop work should replace the unavailable capabilities at the
composition root without moving desktop policies into shared Core or changing
Windows/macOS behavior. Ordinary input uses the framework's native protocols;
no separate input-method implementation or dependency is introduced here.

## Validation

In a disposable Plasma or GNOME session, launch actual Main/Chat with the session's
native input method. Check preedit, candidate placement, Unicode commits and focus
transitions. Both authenticated Hosts should remain healthy without recovery loops.
Global observation, overlays, silent capture and shortcuts must report their
unavailable capabilities rather than borrowing Hyprland's session services.

Use the [Linux acceptance checklist](LinuxHyprlandValidation.md) for shared setup
and cleanup. Ordinary window/input checks do not establish full desktop parity,
distribution coverage or multi-monitor acceptance.