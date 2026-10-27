# Linux / Hyprland port

## Runtime architecture

Main uses `Avalonia.Wayland`, matching the repository's Avalonia version, with
`UseWaylandWithFallback` retaining X11 when native initialization fails.
`LinuxDesktopServices` selects a backend from the inherited desktop session.
See the [desktop adaptation layer](LinuxDesktopLayer.md) for compositor, overlay,
capture, coordinate and ownership contracts and the limited X11/GNOME/Plasma scope.

Hyprland supplies output topology, window identities and geometry. AT-SPI supplies
application semantics, including native Wayland applications. Stacking is an
explicit estimate based on workspace, floating state and focus history; separate
window frame geometry is unavailable. Main's process is excluded from automatic
attachment and picker candidates so its own windows cannot become targets.

`HyprlandWindowingPlatform` decorates the native backend before window creation.
Each owned surface starts with an exact title containing Main's PID and a window
ID; its localized title returns after mapping. Runtime rules target that exact
surface. Chat floats, inherits compositor rounding and retains its placement.
Selectors and passive effects use explicit output bounds and disable their own
blur, shadow, rounding and animation. These rules do not edit saved compositor
configuration or apply to other applications.

Desktop positions remain global logical coordinates; render scaling follows each
output. The bridge translates compositor geometry, floating-window size changes,
activation and topmost state into the existing Avalonia contracts. Compositor
fullscreen metadata determines window state rather than Wayland's map-time
maximized flag. Managed client shadows are disabled to let Hyprland own frame
geometry. Chat's custom measurement honors native minimum and maximum dimensions.

Passive effects require an empty native input region. The pinned Avalonia Wayland
backend exposes this through the UI-side generated surface proxy's exact
`SetHitTestVisible(bool)` overload. It queues work on the Wayland worker rather
than reading worker-side protocol state on the UI thread. The X11 fallback uses
an empty input shape. Full-surface input and full pass-through are supported;
finite nonempty input regions are unavailable. Revalidate this private bridge,
geometry, menus and input transparency when updating Avalonia.

## Selection and accessibility

`LinuxScreenSelectionService` shares a lifecycle for Screen, Window and Element
selection and adds Free mode for screenshots. It handles pointer movement, wheel,
Tab/Shift+Tab, selection keys, confirmation and cancellation. Screenshot mode
starts in Free independently of the previous element-selection mode. Visible,
non-mirrored outputs are frozen before mapping selectors. Cropping uses those
same pixels, preserves native density up to the existing size limit, and supports
negative origins and reverse drags. Selection masks and outlines are excluded.
Leases, captures and native surfaces are released on completion, cancellation
and failure; Chat restores its visibility when capture fails.

AT-SPI initialization accepts only success or already-initialized return codes.
The Automation Host registers an assistive listener and drains GLib on its native
dispatcher. Reading Chromium root attributes activates its lazy renderer tree.
Focus and point searches are bounded, cross logical wrappers without Component,
retain visited proxies for their native lifetime, and validate focused proxies
against the requested window. Search limits are 256 candidates and 500 ms between
native calls; libatspi timeouts remain 800 ms normally and 1500 ms during startup.
An in-progress native call cannot be interrupted by those elapsed-time limits.

Explicit AT-SPI states and structural roles are preserved. Missing coordinate
fields do not discard independently available identity, text or child relations.
Detached embedded providers can retain semantics without a precise hover rectangle.
An unavailable accessibility bus produces an unsupported capability and a
compositor-window fallback instead of calling a native API that can abort the Host.

Text-selection assistance combines AT-SPI caret/selection events with optional
Wayland PRIMARY monitoring through `wl-paste`. PRIMARY observations are text-only,
respect foreground/exclusion checks and do not replace the regular clipboard.
The anchored palette stays transparent until its first frame and asynchronous
native placement complete. Its freeform editor uses normal Wayland activation so
native input methods can supply preedit, candidates and committed text.

## Input, process isolation and account links

Linux peers authenticate Unix socket UID/PID credentials and the executable image.
Hyprland RPC identity uses `HYPRLAND_INSTANCE_SIGNATURE`, allowing independently
launched jobs in the same compositor to share endpoints without joining different
compositor sessions. The port retains upstream RPC notification dispatch. Its
existing Input Host ordering race is a shared upstream issue, separate from the
focused Linux recording path.

Input Host retains one owned binding per chord per installation and refreshes the
relay target after Host recovery. Hyprland's `bind:remove()` removes every binding
with a chord, so inactive owned handles are disabled and their relay commands
cleared instead. Dormant handles can be reused until configuration is replaced.
Lua strings use Lua-compatible escaping. Other owners' bindings are preserved.

`LinuxShortcutListener` implements focused recording through the original
`IKeyboardShortcutScope` contract. It observes Avalonia key events at application
windows and publishes recording changes and completion; the shared editor does
not contain Linux key handlers. The same recorder is available to all Linux
desktop backends, independently of global shortcut support.

On Hyprland the recorder also owns an Input Host capture lease, which pauses only
Everywhere's global bindings and restores them when recording ends. This lease
uses the existing asynchronous desired-state protocol; returning a scope is not
an acknowledgement that the compositor has already applied suspension. The
Host never receives or retransmits the focused recording keys. Cancellation,
focus loss, detachment and Host replacement retain the configured shortcut and
release key subscriptions and the suspension lease. Other desktop backends
record locally without starting an unsupported global-input Host operation.

Web and account links use `LinuxDesktopLauncher` and the session OpenURI portal,
including account actions hosted in native flyouts. The portal connection remains
alive until its response; cancellation/failure returns false. The BCL fallback
passes one argument to `xdg-open` without a shell. OAuth checks callback registration
and browser-launch success before waiting for the callback. A per-user desktop
entry is refreshed for the current executable and registered for the callback
scheme. It does not make a private Everywhere profile the browser's profile.

First-run paths use `SpecialFolderOption.DoNotVerify` before creating directories.
Synchronous Linux Secret Service access runs off the UI thread. Builds without a
valid account endpoint and public client ID disable sign-in explicitly; local
builds can supply `EverywhereCloudConfigurationFile` using the existing release
configuration fields. No private deployment values belong in source control.

## Runtime requirements and validation

Launch inside the chosen session with `HYPRLAND_INSTANCE_SIGNATURE`,
`XDG_RUNTIME_DIR`, `WAYLAND_DISPLAY` and the session D-Bus address available.
`DISPLAY` is needed for the X11 fallback. This backend targets Hyprland's Lua IPC,
binding and window-rule APIs; older configuration APIs require separate validation.

Runtime dependencies include Wayland libraries, AT-SPI (`libatspi.so.0`),
GLib/GObject and `grim` for visible capture. Optional PRIMARY monitoring uses
`wl-paste`; notifications and desktop launching use `notify-send` and `xdg-open`.
The fallback additionally needs XWayland and X11 libraries. Build prerequisites
and submodule/dependency setup are in the [build guide](../build.md).

See the [acceptance checklist](LinuxHyprlandValidation.md) for commands and native
checks. Run them again against the final source revision before claiming runtime
acceptance. Screen captures contain visible desktop pixels, including occlusion;
they do not reconstruct hidden or off-workspace window content.

Applications without accessible semantics retain window-level projections.
Password text is unsupported. Some Chromium fractional-scaling and embedded
Flutter providers expose inconsistent extents; uniform scaling of the entire
accessibility tree cannot safely repair them. Applications without an AT-SPI Text
interface cannot supply the missing text. Physical mixed/rotated monitors and
individual toolkits need native validation; unit tests do not establish universal
coverage. Installation-wide `--hosts-control shutdown` remains Windows-only;
`stop` cooperatively stops Linux Hosts.