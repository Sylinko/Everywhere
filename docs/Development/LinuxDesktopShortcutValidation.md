# Linux desktop shortcuts and local VM controls

## Separate recording from global activation

`LinuxShortcutListener` records focused events through `IKeyboardShortcutScope`.
That contract does not register a chord with a compositor. The original ordinary
desktop starter supplied no global listener, so a valid recording on
Plasma/GNOME/Niri did not produce a global action. This was not a regression in the
focused scope or a reason to restore the removed RpcConnection changes.

Plasma/GNOME and portal-capable X11 sessions now use `PortalShortcutListener` as
the global backend. Hyprland keeps its Input Host/compositor bindings. The portal
uses the existing Tmds.DBus.Protocol dependency; no package is added. Managed
D-Bus approval lives in Main because a pending/declined permission request is a
user decision, not an Input Host transport failure. The starter Input Host stays
healthy and continues to reject unsupported native capture operations.

One worker owns the complete desired set and one portal session. Registering the
same chord fans out to multiple handlers; disposing a lease removes only that
handler. Changes create a new session because BindShortcuts accepts one set per
session. Entering focused recording closes the current session to release its
grabs, and disposing the recording lease restores the desired set. The response
signal is subscribed before each request, and the session is explicitly closed
before disconnecting. Queued activations from a disposed session are discarded.
Denial is logged without an automatic Host restart or permission-dialog loop.

The portal identity matches the packaged `com.Sylinko.Everywhere.desktop` entry.
The test VM supplies that identity for its unpacked build. Preferred keys are a
proposal: native approval, existing desktop conflicts and desktop shortcut
settings remain authoritative. In particular, a chord recorded in Everywhere
must still be granted by the desktop. A permission dialog may appear behind Main
when the desktop cannot associate it with an exported Wayland parent window.

Protocol references:

- https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html
- https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.host.portal.Registry.html
- https://specifications.freedesktop.org/shortcuts/latest/

## Linux activation identity

The real Niri test exposed a second, independent failure. The initial Main ran
in a user service with POSIX SID 24853, whereas the compositor-launched command
ran in SID 24817. The old ordinary-Linux fallback used Process.SessionId, so the
command created a second Main instead of finding the first activation endpoint.
Subsequent commands could activate the second instance, hiding the duplication.

RpcRuntimeIdentity now uses the actual Linux display identity: explicit X11
sessions use DISPLAY, Hyprland retains its instance signature, and ordinary
Wayland uses its socket resolved against XDG_RUNTIME_DIR. Absolute and relative
names of the same socket produce the same endpoint; nested sockets stay separate.
An explicit non-Hyprland desktop does not inherit a parent's Hyprland identity.
Windows/macOS and headless fallback behavior are unchanged. RpcConnection,
request dispatch and notification processing are unchanged.

## Niri boundary

Niri 26.04 in this VM does not offer a usable GlobalShortcuts portal backend.
The VM therefore explicitly binds Ctrl+Shift+E to its Everywhere launcher. A
secondary launch activates Chat through the authenticated activation endpoint,
rather than creating a new application instance. This binding is VM configuration;
changing the chord in the application's editor does not rewrite a Niri config.
Other Niri installations need an equivalent binding to their installed command.

The nested test wrapper selects Niri's actual Wayland socket, sets its desktop
identity and removes an inherited Hyprland marker. Mod+E uses the same environment.
Niri's Mod becomes Alt in nested mode, so its close action is Alt+W there and
Super+W in a standalone session. This avoids the parent compositor consuming the
same Super chord. Standalone DRM remains unavailable on this software GPU VM.

## Modifier pad and logout

The VM's modifier device sends only Ctrl/Shift/Alt/Super. The pad no longer keeps
modifiers latched after the ordinary key is released. A read-only observer of the
VM's QEMU USB keyboard detects chord completion; it never grabs, forwards or logs
physical key events. A udev symlink and device-specific service permission limit
that observer to the test keyboard. Escape releases modifiers too. UI closure
releases immediately; loss of the GUI heartbeat still has a 60-second fallback.
This VM-only observer is not part of Everywhere's global-shortcut backend.

On GNOME, the pad uses XWayland's non-focusable/above window hints because GNOME
does not provide layer-shell. Other supported Wayland compositors use a
non-keyboard-interactive layer surface. The application itself remains native
Wayland. This keeps the pad reachable above a maximized application without
redirecting typing away from the focused editor.

The minimal Hyprland configuration originally omitted a close-window binding.
Super+W now closes the active window, retaining Super+Shift+Q for session exit.
KDE's existing Super+W opens Overview, and its Alt+F4 close binding is retained.
A compositor action must exist for a successfully delivered modifier to do work.
The automatic release avoids being stuck behind Overview while Super is held.

The Hyprland logout button originally ran uwsm stop even in a plain GDM Hyprland
session. It now calls the installed Lua dispatcher hl.dsp.exit(), works with
managed/plain sessions and checks the command result. Niri uses its own IPC quit
command. A failed request is displayed instead of silently closing the dialog.

## Verification on 2026-10-28

The self-contained linux-arm64 publish succeeded. Linux tests passed 97/97,
including portal key syntax, callback leases, capture suspension and Hyprland
regression cases. Process-isolation tests passed 68/68, including five Linux
session-identity cases. Evidence: linux-portal-session.trx and
process-isolation-display-session.trx in the sibling VM test-results directory.
The historical full Core suite was not rerun, and its previously reported failures
are not claimed fixed.

Actual local NixOS VM checks include:

- Plasma Wayland: native approval, Ctrl+Shift+E from Dolphin, re-recording the same
  chord, and activation again afterward. The modifier pad releases automatically.
- Plasma X11 after the generation-6 guest reboot: Ctrl+Shift+E opens Chat from
  the desktop with native window-manager decorations still present.
- GNOME Wayland: native approval of the configured chords, Ctrl+Shift+E from the
  desktop, and Ctrl+F in Chat after clicking the non-focusable modifier pad.
- Nested Niri: the first and repeated Ctrl+Shift+E open Chat under the same Main
  PID, including when Main starts in a different POSIX session. Mod+W closes Foot.
- KDE Overview: virtual Super+W triggers its native action, and the modifier state
  becomes empty automatically even while Overview hides the pad.
- Hyprland: virtual Super+W closes Foot; the new build still opens Chat through
  Ctrl+Shift+E. Mouse logout exits to GDM in both plain and uwsm-managed sessions.

Ordinary test keys were injected through QMP into QEMU's keyboard input path;
manual macOS Screen Sharing input was not separately repeated. No model request
or authenticated cloud operation was performed. Physical GPU, standalone Niri
DRM, full selection/capture and input-method acceptance remain outside this check.

## Permission cancellation regression tests

The explicit `PortalShortcutSessionTests` fixture requires `dbus-daemon` and a
private session bus. It owns a synthetic portal that deliberately leaves shortcut
approval pending; it does not use the desktop's portal or show native dialogs.
Run it separately from the normal Linux suite:

```sh
dbus-run-session -- sh -c '
    export EVERYWHERE_PORTAL_TEST_BUS="$DBUS_SESSION_BUS_ADDRESS"
    dotnet test tests/Everywhere.Linux.Tests/Everywhere.Linux.Tests.csproj -c Release \
        --filter FullyQualifiedName~PortalShortcutSessionTests
'
```

The tests verify that removing a registration closes the pending session, and that
recording closes it before restoring the same registration set after the lease
ends. This exercises the real D-Bus request/response and session-close paths;
it does not establish compositor permission UI or native key activation support.
