# Linux desktop rendering validation

## Scope and environment

Validated on 2026-10-27 in a local Apple Silicon QEMU/HVF virtual machine, with
NixOS 26.05 ARM64, Plasma 6.6.6 and GNOME 50.5. Both desktops ran actual Wayland
sessions. The VM has 4 CPUs, 8 GiB RAM, an 80 GiB sparse disk and a virtio GPU;
Mesa reports llvmpipe software rendering. An isolated ARM64 .NET SDK container
produces the self-contained linux-arm64 application; the application runs in the VM.

The feature branch starts at linux-hyprland commit 83aa0be7. No RPC implementation,
release workflow, authentication configuration or Hyprland backend was changed.

## Reproduced problems and changes

- Main and Chat exposed the wallpaper or other windows through their backgrounds.
  ShadWindow uses theme colors with alpha, but its template does not supply the
  framework transparency fallback layer. StandardDesktopWindowAppearance binds an
  opaque version of the appropriate theme color and follows runtime theme changes.
- Main lacked ordinary window controls. It now uses full framework decorations on
  Plasma/GNOME. The drawn title bar receives an opaque theme background as well.
  Initial finite dimensions are bounded by the screen working area.
- The borderless Chat lacked dependable resize grips. Its edges now request native
  resizing through BeginResizeDrag, with resize cursors and a visible theme border.
- Chat returned to its default dimensions during resizing. Avalonia.Wayland reports
  compositor resize events with WindowResizeReason.Layout. The existing Core latch
  accepted only User, so MeasureOverride discarded the new dimensions. Layout is
  now accepted only while SizeToContent is explicitly Manual; existing automatic
  sizing and minimum-size reset behavior remain in place.
- On KWin, hiding and reopening Chat added a second title bar. In Avalonia 12.1.3,
  WindowImpl.Sink recreates a toplevel and applies its initial decoration mode but
  does not restore the existing sticky client-decoration request. Simply setting
  WindowDecorations again is ineffective because the platform setter returns early
  after that latch is set. The standard-desktop setup uses the public experimental
  ForceDrawnDecorations option with a narrowly scoped diagnostic suppression.
  This keeps Main and Chat decorations consistent across Hide/Show without private
  reflection or KWin rules. Revisit this workaround when upgrading Avalonia.

Appearance bindings are installed once per window and do not keep closed windows
alive. The shared desktop appearance is selected for X11 and standard Wayland
desktops, including Plasma, GNOME and Niri. Hyprland keeps its existing appearance.
ForceDrawnDecorations is configured only for standard Wayland; X11 continues to
negotiate native window-manager decorations.

## Actual desktop checks

| Check | Plasma Wayland | GNOME Wayland |
| --- | --- | --- |
| Actual desktop session and native Wayland application | Passed | Passed |
| Opaque Main/Chat backgrounds over another window | Passed | Passed |
| Light/dark appearance and theme changes | Passed | Passed |
| Main window controls and resizing/maximizing | Passed | Passed |
| Chat edge/corner resizing without default-size snapback | Passed | Passed |
| Chat Hide/Show preserves size and avoids a second title bar | Passed | Passed |
| Keyboard input retained in an unsent draft | Passed | Passed |

The final decoration build was checked in both desktops, including dark-theme
Hide/Show and resizing. Light backgrounds/theme switching were also checked during
implementation, and the final automated test covers live resource/theme updates.
No model request or authenticated cloud operation was used for these checks.

The VM uses GDM so GNOME no longer emits the screen-locking-unavailable warning
seen under SDDM. The dedicated empty-password test account can require a second
Enter for the additional KWallet PAM conversation. SSH and VNC bind to host loopback.

## Automated checks and baseline comparison

- Final Linux suite: 87 passed, 0 failed, including opaque theme updates and the
  unchanged Hyprland appearance check. Evidence: linux-csd-final.trx.
- Full shared Core suite: 642 passed, 6 failed, total 648. This is not a passing
  full-suite result. Evidence: core-wayland.trx.
- A focused comparison ran the same nine affected/explanatory cases with the
  original ChatWindow source from 83aa0be7, then the feature source in the same
  container. Five failures occur in both. ActiveCapture was a timeout in the
  feature run and passed in the baseline run; treat that additional result as
  unresolved/intermittent rather than claiming identical suites.

| Core failure | Observed evidence |
| --- | --- |
| ReadText_WhenCompositeCrossesMemberBoundary_ContinuesThroughObservedFallback | Existing fixture paging/newline assumption also fails with baseline ChatWindow on Linux |
| InitializeAsync_TavilyApiKeyChanges_PersistsSelectionAcrossReload | SDK test container lacks libsecret-1.so.0; also fails with baseline source |
| CaptureNotifications_ForActiveCapture_UpdateAndFinishScope | Input Host timeout also fails with baseline source |
| RegistrationsAndNotifications_WithDuplicateHandlers_SnapshotOnceAndFanOutLocally | Input Host timeout also fails with baseline source |
| AccountAction_WithoutWindow_UsesApplicationLauncher, GetSupportCommand case | Test build lacks configured AccountBaseUrl; also fails with baseline source |
| ActiveCapture_WhenConnectionDisconnects_IsCancelledLocally | Timeout in full/feature runs, passed in baseline comparison |

The original ChatWindow was mounted read-only for the comparison; the feature
source was rebuilt afterward to prevent reuse of baseline build outputs. RPC was
left untouched rather than changing it to make unrelated tests pass.

Host-side build logs, TRX files, desktop screenshots, NixOS configuration, launch
scripts and decision records are retained in the sibling everywhere-nixos-desktop-vm
directory. Examples: plasma-csd-reopen-resize.png, gnome-csd-reopen.png and
plasma-chat-ready.png. The VM desktop menu exposes Everywhere Desktop Test with
separate Plasma/GNOME test data directories.

## Remaining acceptance boundaries

This validates Main/Chat window rendering in a software-rendered VM. It does not
establish physical-GPU coverage, fractional scaling, multiple monitors, IME
preedit/candidate placement, Windows/macOS runtime acceptance or full desktop parity.
Global shortcuts, selection and passive overlay capabilities remain explicitly
unported in the standard Wayland starter. Cloud login is unavailable in this local
build because official account build constants are absent.

The existing fixed-size welcome guidance can clip in a small restored Main window;
maximizing makes it operable. Its shared dialog layout has not been changed here.

## Follow-up: frame gap, Hyprland regression and Niri

The earlier visual acceptance missed an 8-DIP transparent band between Main's
client area and the drawn frame. ShadUI sets DefaultShadowThickness to 8 but does
not paint a shadow. Avalonia includes that thickness in the client/decorations
insets, which exposes the wallpaper in the unused area. The standard-desktop
style now sets DefaultShadowThickness to zero. This is scoped to the application's
standard desktop setup; no third-party submodule is modified.

Main shares Chat's 8-DIP client-edge resize gestures so removing the empty reserve
does not remove the useful resize target. Actual GNOME dragging expanded a restored
Main from approximately 927x575 to 1037x647, with no transparent band. Both restored
Plasma and GNOME windows were visually checked against the earlier screenshots.
Evidence includes frame-baseline.png, frame-fixed-plasma-main.png,
gnome-frame-no-gap.png and gnome-final-main-edge-resize.png.

Niri is explicitly detected through the inherited desktop name and selects the
same standard toplevel backend. IsStandardWayland groups the three implemented
standard desktop routes; inherited Hyprland markers cannot override an explicit
Niri desktop. Global capabilities remain unavailable, exactly as on Plasma/GNOME.

Actual Niri testing exposed another sizing issue: automatic Chat content sizing
rejected compositor tiling requests and kept returning 400x300. Standard Wayland
windows now start with manual sizing and accept subsequent Layout configure
events. A resize event handler restores manual mode before Chat's shared latch
runs, including after its minimum-size automatic reset. Hyprland is excluded from
this policy. Niri accepted a 732x596 tiled Chat, native floating edge dragging,
a 400x300 minimum followed by expansion to 600x450, keyboard text entry and
Hide/Show without a second title bar. Niri controls whether a remapped window is
tiled or floating; the app does not pretend to preserve compositor-owned mode.

### VM coverage and limits

The local VM now installs Hyprland 0.55.4 and Niri 26.04 alongside Plasma/GNOME.
Actual Hyprland sessions ran the saved 30b15575 build and the candidate build:
Main/Chat rendered, Ctrl+Shift+E opened Chat, keyboard input worked, and two Host
RPC connections authenticated. The final source changes are still excluded from
Hyprland's appearance/resize backend. This is focused rendering/input regression
coverage, not a repeat of the full selection/capture/IME acceptance matrix.

Niri's independent DRM session did not produce an output on this VM. The matching
Niri source explicitly rejects software EGL devices, and its logs report
"software EGL renderers are skipped" followed by "no allocator available for device".
Its IPC output map was empty; Avalonia then failed because no wl_output existed.
No application workaround is used to conceal that environment failure.

Niri UI acceptance instead used a real nested Niri compositor (winit backend)
inside the VM, first on Hyprland and then through the desktop helper on GNOME.
The app connected to Niri's own Wayland socket and logged NiriWayland, not Hyprland.
Niri's IPC listed the application's windows and accepted native layout actions.
This validates the Niri application path while leaving standalone DRM and physical
GPU acceptance outstanding. The VM provides an Everywhere in Niri (Nested) menu
entry and start-niri-test helper for this lane. Global shortcuts/overlays/capture
and full input-method acceptance are still future Niri integration work.

Final Linux regression: 89 passed, 0 failed (linux-standard-final.trx). The two
additional cases cover Niri identity and inherited Hyprland-marker isolation.
The historical Core failures above were not changed or rerun in this follow-up;
this follow-up does not modify shared Core or RPC code.

### NixOS rollback practice

The original verified system is generation 2 and is protected by the
/nix/var/nix/gcroots/everywhere-pre-compositors GC root. The new configuration was
built, temporarily activated with test, reverted to that exact baseline closure,
then restored to the candidate before promotion. The verified multi-desktop
configuration is generation 3; boot entries 1, 2 and 3 remain available.

Hyprland/Niri test configurations are managed under /etc/everywhere-vm by the
NixOS closure, and user config paths point there. Source configuration checkpoints
are kept separately under /etc/nixos/checkpoints. The host rollback-vm.sh restores
both the baseline source configuration and its system profile before switching.
NixOS rollback does not revert home directories or application data: immutable
application build directories, a current build symlink and separate per-desktop
XDG test profiles provide the application comparison lane.

## Follow-up: X11 controls and the remaining KWin frame seam (2026-10-28)

The earlier appearance setup excluded X11. Consequently its Main still extended
content into the decorations and retained alpha in its background. The shared
appearance and resize helpers now live in Everywhere.Linux.Desktop and apply to
all supported desktop routes except Hyprland. On Plasma X11, Main now requests
full native decorations with no client-area extension; Main and Chat use the same
theme-aware opaque backgrounds already exercised on standard Wayland.

A separate 1-DIP transparent strip remained on KWin Wayland after removing the
shadow reserve. Painting only PART_TitleBar left the enclosing decoration border
unpainted. StandardDesktopStyles now paints PART_UnderlayWrapper instead, filling
the complete drawn decoration area with the existing DialogBackgroundColor.
Actual Plasma Wayland inspection confirms that the top seam is filled.

The self-contained linux-arm64 build succeeded. The Linux suite passed 90 tests,
including a new X11 opaque-background/full-decoration case, live theme updates and
Hyprland appearance isolation (linux-x11-chrome.trx). Shared Core and RPC sources
were not modified, and the historical Core suite was not rerun in this follow-up.

The local VM additionally provides mouse-operated Waybar panels on Hyprland and
nested Niri, an application grid, a confirmation dialog for session exit, and a
modifier-only uinput keyboard. The keyboard UI retains the application's keyboard
focus and can latch multiple modifiers while ordinary QEMU/VNC keyboard events
supply letters, numbers or Enter. Actual checks include Ctrl+F in Plasma X11 and
Wayland, Ctrl+Shift+E opening Chat in Hyprland, and Alt+Enter starting Foot in
nested Niri. The ordinary key events were sent through QMP to the QEMU keyboard
used by VNC; manual macOS Screen Sharing input was not separately repeated.
Releasing or closing the UI releases its modifiers. These VM tools are separate
from the application and do not implement global shortcuts on Niri/Plasma/GNOME.
Evidence: x11-chrome-ready.png, x11-virtual-ctrl-f-passed.png,
x11-modifier-release-passed.png, plasma-virtual-ctrl-f.png,
plasma-modifier-release.png, hyprland-virtual-ctrl-shift-e.png,
niri-virtual-alt-return.png and niri-mouse-applications.png in the VM directory.

The starting VM had a user-created generation 4 with fastfetch installed; that
configuration was preserved and pinned as everywhere-pre-desktop-tools. The new
system was activated with test before promotion, with complete configuration
sources saved under /etc/nixos/checkpoints/with-desktop-tools. The host helper
rollback-desktop-tools.sh restores the generation-4 system and its source files.
This rollback still does not revert home directories or application builds.
The verified desktop-tools configuration is generation 5. A guest reboot loaded
the exact promoted closure, started the modifier service, retained fastfetch and
kept generation-4 and earlier boot entries. Post-reboot Plasma X11 rendering,
Ctrl+F and modifier release were verified again.

Subsequent global shortcut registration and the Linux activation-session identity
fix are documented in [desktop shortcut validation](LinuxDesktopShortcutValidation.md).
Those changes retain RpcConnection message handling and the rendering policy above.