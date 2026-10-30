# Linux / Hyprland acceptance checklist

Run these checks against the source revision under review. Record results outside
the production source tree; previous runs do not establish acceptance of a later
revision. The unit suite and native desktop checks cover different boundaries.

## Build and unit tests

Use the SDK pinned in `global.json`, initialize submodules and Git LFS, and restore
packages as described in the [build guide](../build.md). On Linux:

```sh
dotnet restore Everywhere.Linux.slnx
dotnet build src/Everywhere.Linux/Everywhere.Linux.csproj -c Release
dotnet test tests/Everywhere.Linux.Tests/Everywhere.Linux.Tests.csproj -c Release
dotnet test tests/Everywhere.Automation.Tests/Everywhere.Automation.Tests.csproj -c Release
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj -c Release
dotnet test tests/Everywhere.ProcessIsolation.Tests/Everywhere.ProcessIsolation.Tests.csproj -c Release
```

The Linux suite covers session selection, geometry transforms, IPC bounds,
AT-SPI role/state mapping, PRIMARY handling, desktop capability contracts and
window policies. Shared tests cover missing-geometry semantics, shortcut recording,
account-link routing and the original duplex RPC request/response behavior.
Linux tests also exercise recording without a global backend and without a
shortcut editor. The upstream Input Host ordering tests can still fail because
this port deliberately retains upstream RPC dispatch; report those separately
from focused-recording and native compositor acceptance.

## Controlled desktop setup

Use a disposable Hyprland session and application profile for acceptance. A nested
session needs its own runtime directory, D-Bus session, Wayland socket and XWayland
display; a separate D-Bus address alone does not isolate AT-SPI sockets. Pass the
selected compositor instance explicitly. Keep screenshots, logs, fixtures and
local automation outside the repository.

Launch actual Main and Chat, confirm both authenticated Hosts remain healthy, and
include a fresh profile whose XDG config/data directories do not exist yet.
Use controlled GTK, Qt and browser content with named controls and editable
Unicode text. Native provider behavior must be checked separately from synthetic
contract tests. Inspect compositor clients after each operation to confirm that
selectors disappear and owned window rules do not affect unrelated applications.

## Native checks

1. Open, hide and reopen Chat; create a new chat and verify saved client size and
   placement. Exercise output changes, fractional scaling, negative origins and
   mixed-density outputs. Check minimum/maximum size, rendering and native focus.
2. Invoke element selection and screenshot selection from the real attachment
   menu. Exercise Screen, Window, Element and Free modes, wheel/keyboard mode
   changes, forward/reverse drags, confirmation, Escape and right-click cancellation.
   Verify attachment admission and that Chat returns after failure or cancellation.
3. Compare a confirmed screenshot crop with pixels captured before overlays map.
   It must exclude masks/outlines and preserve density within the capture limit.
   Test a failing or missing capture helper and repeated cancellation/remapping.
4. Place a passive effect above a controlled button. Click through it and verify
   that the underlying button receives the click without losing focus. Check that
   only Everywhere's own passive surfaces receive empty native input regions.
5. Read accessible labels, Unicode text and nested controls from GTK/Qt/browser
   windows. Exercise deep/wide/logical-wrapper trees, missing leaf geometry,
   managed descendants, stale proxies and foreign-window focus. Test an unavailable
   accessibility bus in a separate process and verify clean capability failure.
6. Record shortcuts with each modifier, modifiers released before the main key,
   Escape, partial chords, focus loss and detachment. Confirm partial/cancelled
   recordings preserve the old setting. Restart Input Host and verify registrations
   recover without duplicate callbacks or changing another application's bindings.
7. Open Chat and the text-selection editor with a native input method. Verify
   preedit, candidate placement, committed Unicode and reactivation after focus
   changes. Check repeated selection, foreground exclusions, toolbar placement,
   outside-click dismissal and optional PRIMARY monitoring without clipboard changes.
8. Open a public link through Main and account actions hosted in a flyout. Verify
   use of the normal desktop browser profile, callback registration for the current
   executable, and explicit handling of launch/portal failures. Use configured
   public account endpoints only when testing login itself.

Afterward, close disposable Main/Host processes, release selectors/captures and
confirm the original desktop/profile remains usable. Keep toolkit and monitor
limitations explicit in the review; no model request is needed for these checks.

## Other desktop backends

For [X11](LinuxX11Starter.md), validate EWMH foreground/stacking, frame extents,
visible root pixels and ordinary input at the supported scale/layout. For
[GNOME/Plasma Wayland](LinuxWaylandStarter.md), validate ordinary Main/Chat windows,
input methods and healthy Hosts, and check that unavailable global capabilities
fail explicitly before creating an overlay.