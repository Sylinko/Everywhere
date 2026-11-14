# Linux desktop adaptation layer

## Scope and ownership

The repository still builds separate platform packages from one source tree.
This adaptation layer is compiled only by `Everywhere.Linux`. Shared Automation,
Core and RPC integration support geometry failures, placement, session identity
and input through their existing cross-platform contracts. RPC notification
dispatch remains unchanged from upstream. Hyprland retains the full current
integration. A minimal EWMH X11 backend and
ordinary Plasma/GNOME/Niri Wayland windows are now available as contributor starting
points. The latter intentionally provide no global state, selection overlays or
silent capture. Plasma 5 uses KGlobalAccel on X11 and Wayland. Plasma 6/GNOME and
portal-capable X11 sessions use the user-approved GlobalShortcuts portal; Niri
still requires an external compositor binding and reuses the standard Wayland UI
backend.

`Desktop/LinuxDesktopServices.cs` is the per-process composition root. It selects
the explicitly inherited desktop backend, constructs its three services and
installs the native windowing bridge when required, before creating UI windows. An unsupported
session fails explicitly rather than discovering an arbitrary compositor socket.
The same composition root is used by Main and Automation Host. Input Host shortcuts and global overlay-input observation retain their
existing shared contracts and compositor-specific implementations.

## Responsibilities

| Contract | Common consumers | Hyprland implementation |
| --- | --- | --- |
| `ICompositorStateProvider` | `LinuxVisualElementBackend`, AT-SPI window-relative geometry, text-selection foreground checks, picker output topology | `HyprlandCompositorStateProvider`: bounded, cancellable IPC; workspace visibility; monitor transforms; opaque identities |
| `IOverlayWindowController` | `LinuxWindowHelper`, `LinuxScreenSelectionService` | `HyprlandOverlayWindowController`: maps presentation roles to `HyprlandWindowState`; owned rules, placement, stacking and native input policy |
| `ISilentScreenCapturer` | Automation element captures and frozen picker frames | `HyprlandSilentScreenCapturer`: captures the explicitly named output using `grim -o`; bounds output size, reply size and helper lifetime |

AT-SPI traversal, application matching, native reference ownership, query limits
and identity retention remain common Linux code. `CompositorVisualElement`
represents output/window fallback targets without referring to Hyprland's JSON
models. `LinuxScreenSelectionService` owns purpose/mode history, leases, drawing,
confirmation and cleanup. `LinuxScreenCapture` owns output composition and the
transport pixel buffer. None of those consumers creates a Hyprland connection.

## Geometry and observation strength

All desktop rectangles and pointer coordinates are global **logical** compositor
units. `PixelRect`/`PixelPoint` retain the existing integer Automation coordinate
contract; their names do not imply physical output pixels. Surface input regions
are **surface-local logical** rectangles. `OutputInfo.Scale` separately describes
pixel density. `OutputCoordinateMap` maps between the UI backend's output space
and this desktop space without scaling a global origin.

`CompositorWindowInfo.ClientRect` is the origin for AT-SPI `WINDOW` coordinates.
`FrameRect` includes server decorations only when observed; it is nullable.
Selection uses `FrameRect ?? ClientRect`. Hyprland's current `clients` observation
exposes `at`/`size`, so its provider explicitly leaves `FrameRect` unavailable.
Do not estimate decoration extents or move the AT-SPI origin to a guessed frame.
Window IDs and handles are opaque, session-scoped identities, never native pointers.

`GetWindowsInStackingOrderAsync` returns visible application windows from top to
bottom with an explicit `StackingOrderKind`. The current Hyprland implementation
preserves the existing special-workspace/floating/focus-history estimate. It is
not an authoritative render stack; overlapping windows can remain ambiguous.
A backend with real stack information should report `Compositor`. Observations
are finite reads, not an atomic transaction across separate compositor requests.

## Threading and lifetime

State reads use `ValueTask` and honor cancellation. Hyprland IPC distinguishes
caller cancellation from its own two-second deadline. Providers must not capture
a UI synchronization context because shared Automation APIs are synchronous.
The synchronous bridge is localized to `LinuxVisualElementBackend` and native
selection-event handling; AT-SPI calls still run on their existing native dispatcher.

Overlay policy setters run synchronously on the UI thread, matching
`IWindowHelper` and allowing configuration before the first map. They queue native
policy updates; they do not await a UI continuation or assert compositor completion.
Placement acknowledgement remains `IWindowPresentation.WaitForPlacementAsync`.
The separate `WaitForHiddenSurfacesAsync` barrier waits for unmaps before frozen
pixels are requested. Placement changes do not reset keyboard or pointer policy.

`WaylandSurfaceInput` is the reusable native input bridge. The pinned
Avalonia.Wayland 12.1.3 UI-side `SurfaceProxy.SetHitTestVisible` queues
input changes on the owning Wayland worker. That worker calls
`wl_surface_set_input_region`, commits the change and reapplies it on reconnect.
`null` means full-surface input; an empty rectangle means pass-through. A finite
nonempty rectangle currently throws `NotSupportedException`. Existing callers
need only full/empty regions. Arbitrary regions require an upstream worker/proxy
API extension; directly touching `wl_surface` on the UI thread is unsafe.

The capture contract returns the existing owned `IVisualElementCapture` buffer,
rather than a UI `Bitmap`: Automation Host has no Avalonia renderer. Main converts
buffers only when displaying frozen frames. Captures retain logical Bounds,
BGRA/premultiplied pixel format and native density up to the existing size limit.
Composition disposes each source even on cancellation or topology changes. Frozen
frames are acquired before mapping selectors and released with their session.

## Adding another desktop

Provide implementations of the three contracts and connect them in the composition
root, together with that desktop's native windowing/placement and input integration.
Keep compositor IDs, workspace rules and privileged APIs inside the backend.
Retain the common AT-SPI semantics, selection lifecycle, crop policy and shared RPC.
A compositor with no global positioning API needs an actual placement mechanism;
ordinary Wayland toplevels cannot satisfy the controller by assigning `Window.Position`.
Do not treat a portal's presence as silent-capture permission. Report unavailable
capabilities explicitly until a permitted capture path exists.

Acceptance for each backend must include controlled GTK/Qt/browser trees, frame
versus client coordinates, window overlap, fractional/negative/mixed output
geometry, keyboard focus transitions, actual input transparency, frozen capture
and cancellation/remap cleanup. Unit tests and synthetic providers cover contract
semantics; they do not establish another desktop's runtime support.

## Native implementation references

The fixed-version Avalonia [UI-to-worker contract](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Wayland/Server/Persistent/IWSurface.cs)
and [surface implementation](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Wayland/Server/Persistent/WSurface.cs)
are the source of the worker/reconnect/input-region boundary. Revalidate this
private bridge before updating Avalonia.Wayland.

## Validation

Use the [Linux acceptance checklist](LinuxHyprlandValidation.md) to validate native
geometry, input, accessibility, capture and session cleanup. Unit tests establish
contract behavior; they do not establish runtime support for another desktop.

## Additional starter backends


## X11 backend

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
assistance remain unported. Plasma 5 global keyboard shortcuts use KGlobalAccel; other supported desktops use
the user-approved GlobalShortcuts portal when available; global mouse shortcuts remain
unsupported. Focused shortcut recording uses the common `LinuxShortcutListener`
implementation and `IKeyboardShortcutScope`, while the authenticated Input Host
remains healthy and idle.

An explicit X11 session takes precedence over inherited Wayland/Hyprland markers.
Main selects Avalonia X11 directly rather than falling back from Wayland.

Validate ordinary Main/Chat windows, EWMH foreground/stacking, client/frame
geometry, visible root-pixel capture and ordinary input in a disposable profile.
Follow the [Linux acceptance checklist](LinuxHyprlandValidation.md). The supported
single-screen scale/pixel-layout boundary must be retained when reporting results.
## Ordinary Wayland backends

Plasma, GNOME and Niri Wayland start through Avalonia.Wayland's ordinary toplevel and text-input
implementation. It does not install `HyprlandWindowingPlatform`, issue compositor
commands, assign guessed global coordinates or reuse Hyprland's input bindings.
`StandardWaylandWindowController` permits ordinary floating-application intent
while explicitly rejecting overlay roles and unsupported global window policies.

`UnavailableWaylandStateProvider`, `UnavailableWaylandScreenCapturer` and
`UnavailableWaylandSelectionService` mark global observation, silent capture and
interactive picking as unported. The selection service rejects requests before
creating a surface. Text-selection assistance remains unported. Plasma 5 registers
global keyboard shortcuts through KGlobalAccel on both X11 and Wayland. Plasma 6,
GNOME and portal-capable X11 desktops use the user-approved XDG GlobalShortcuts
portal. Niri still requires a compositor binding to the application command;
recording a chord does not create that binding.
Focused shortcut recording uses `LinuxShortcutListener` and the shared
`IKeyboardShortcutScope` contract; it requires no compositor-specific keyboard
hook. Portal approval is managed separately from the Input Host so user denial
does not repeatedly restart a healthy Host.

This is a starting point for desktop contributors, not feature parity with
Hyprland. Future desktop work should replace the unavailable capabilities at the
composition root without moving desktop policies into shared Core or changing
Windows/macOS behavior. Ordinary input uses the framework's native protocols;
no separate input-method implementation or dependency is introduced here.


## Shortcut backends and activation identity


`LinuxShortcutListener` records focused events through `IKeyboardShortcutScope`.
That contract does not register a chord with a compositor. Global activation is supplied separately by the selected desktop backend.

Plasma/GNOME and portal-capable X11 sessions use `PortalShortcutListener` as the
global backend. Plasma 5 registers through KGlobalAccel on both X11 and Wayland;
Plasma 6/GNOME and other portal-capable desktops request GlobalShortcuts approval.
Validate the native KGlobalAccel registration on Plasma 5 and portal approval only
on portal-backed desktops. Hyprland keeps its Input Host/compositor bindings. The portal
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

Activation endpoints use display identity, not the POSIX process session ID,
which can differ between a user service, terminal job and desktop launch.
Explicit X11 sessions use DISPLAY with an omitted screen normalized to screen
zero. Wayland uses the socket resolved against XDG_RUNTIME_DIR, including on
Hyprland: compositor-specific variables may be absent in desktop launchers.
Absolute and relative names of the same socket share an endpoint; nested sockets
remain separate. The Hyprland instance signature is a fallback when no Wayland
socket is available. Windows/macOS behavior and RPC notification dispatch are
unchanged.

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