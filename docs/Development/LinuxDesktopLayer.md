# Linux desktop adaptation layer

## Scope and ownership

The repository still builds separate platform packages from one source tree.
This adaptation layer is compiled only by `Everywhere.Linux`. Shared Automation,
Core and RPC changes support geometry failures, placement, shortcut recording
and ordered input notifications through their existing cross-platform contracts. Hyprland retains the full current integration. A minimal EWMH X11 backend and
ordinary Plasma/GNOME Wayland windows are now available as contributor starting
points. The latter intentionally provide no global state, selection overlays,
silent capture or global shortcuts. niri remains unimplemented.

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

See [X11](LinuxX11Starter.md) for restored EWMH/native-pixel designs and
[ordinary Wayland desktops](LinuxWaylandStarter.md) for the limited Plasma/GNOME
window and input scope. The shared selection lifecycle does not manufacture
capabilities for backends that have not implemented their three services.