# Physical Linux desktop acceptance, 2026-10-29

## Scope and current state

This run uses the physical ASUS test machine and the persistent Ventoy images,
not the earlier NixOS VM fixtures. Tests run in this order: Ubuntu 24.04,
Ubuntu 25.10, Debian 14/testing, then Fedora 44. The feature branch remains
`feat/linux-desktop-backends`; RPC and Windows/macOS production code are unchanged.

Fedora acceptance is pending. Its SSH server accepts the pinned test public key
and then closes the connection. The offline password change may have replaced
`/etc/shadow` without retaining its SELinux label; this must be checked from Arch.
Do not describe Fedora tools or application behavior as accepted until access is
restored and the remaining tests have run.

## Confirmed defects and minimal corrections

### Plasma 5.27 does not implement the requested binding through its portal

On Ubuntu 24.04.5, Plasma 5.27.12 and xdg-desktop-portal-kde 5.27.11 reproduce the
reported behavior: opening the system shortcut settings is followed by an empty
granted-shortcut response. The corresponding KDE implementation opens System
Settings in `BindShortcuts` without registering the supplied actions.
See the [Plasma 5.27 implementation](https://github.com/KDE/xdg-desktop-portal-kde/blob/Plasma/5.27/src/globalshortcuts.cpp).

Only the legacy KDE 5 path now uses the existing KGlobalAccel D-Bus service.
The native service supports both KDE's X11 and Wayland sessions. The existing
listener still owns registration coalescing, recording suspension and cleanup;
the shared `IKeyboardShortcutScope` and RPC implementation are unchanged.
The backend reports conflicts instead of silently claiming a successful grab.

The alternative was to keep KDE 5 explicitly unsupported. Native registration
was selected because the physical probe and the application both received the
actual injected Ctrl+Shift+E key. Newer KDE and GNOME retain the portal path.
No new NuGet dependency or system-wide key interception was introduced.

### DEB and AUR desktop identity did not match portal registration

Ubuntu 25.10 with Plasma 6.4.5 and portal 1.20.3 reports:

```text
Could not register app ID: App info not found for 'com.Sylinko.Everywhere'
```

The DEB installed `Everywhere.desktop`, while the application registers
`com.Sylinko.Everywhere`. Installing the matching desktop file alone lets the
unchanged release reach the real desktop authorization dialog. After approval,
Ctrl+Shift+E activates Chat on the physical Wayland session.

DEB and the downstream AUR reference template now install
`com.Sylinko.Everywhere.desktop`. All Linux desktop entries include
`StartupWMClass=Everywhere` for window association. The independent AUR repository
has not been changed. Ignoring the registration error or falling back to an
anonymous application was rejected because it hides a packaging defect and
weakens the desktop's stable application identity.

### Debian 14's ICU runtime was absent from DEB alternatives

The physical Debian image has `libicu78` 78.3-2. The original release cannot be
installed because it only permits ICU 74, 76 or 72. The corrected alternatives
include 78 while retaining the older supported distributions. The actual fixed
DEB installs and starts on Debian GNOME; no old ICU package, fake library symlink
or development package was required.

## Physical observations

| System | Session | Evidence |
| --- | --- | --- |
| Ubuntu 24.04.5 / Plasma 5.27.12 | Wayland | Original portal failure reproduced; native probe received input; candidate Ctrl+Shift+E displayed Chat. |
| Ubuntu 24.04.5 / Plasma 5.27.12 | X11 | Fixture lacked `kwin-x11`; installing it restored normal decorations. Native registration succeeded and Chat mapping changed after input. |
| Ubuntu 25.10 / Plasma 6.4.5 | Wayland | Missing app-info error reproduced. Matching desktop identity enabled approval and actual Chat activation. Hiding Chat removed it from KWin's managed-window list. |
| Ubuntu 25.10 / Plasma 6.4.5 | X11 | Clean KWin session and portal registration verified. The first screenshot was taken before registration completed; a timing-controlled activation recheck remains desirable. |
| Debian 14/testing / GNOME 50.5 | Wayland | Original DEB dependency error reproduced. Fixed DEB installed; GNOME approval and physical Ctrl+Shift+E displayed Chat over Ghostty running fastfetch. |
| Fedora 44 | Pending | Physical SSH authentication closes after key acceptance; requires offline inspection before acceptance. |

Plasma cannot be assessed without its window manager. Initial session switching
also retained an older Wayland session while SSH kept the user manager alive;
subsequent tests used complete physical reboots. Those fixture failures were not
treated as application-rendering defects.

The hidden-window report was investigated through KWin's actual window metadata.
On the tested Plasma 6 session, the visible Chat was a normal toplevel, but it
disappeared after hiding; only Main remained. The startup shortcut consent window
could also be behind Main. This run does not establish a generic Wayland
`ShowInTaskbar=false` implementation. No compositor rules or forced ownership
were added: ownership can prevent Chat from working while Main is hidden.

## Tools and test-machine changes

Fastfetch and Ghostty 1.3.1 are installed on both Ubuntu images and Debian.
Native fastfetch versions differ by distribution. Ubuntu/Debian Ghostty packages
come from the community package source listed in the
[Ghostty installation documentation](https://ghostty.org/docs/install/binary).
Fedora installation remains pending.

The `pasical` local login password was set as requested; its value is not recorded
in scripts or this report. SSH remains key based with pinned server host keys.
The former shadow file is retained inside each image for rollback.

GNOME's idle lock was temporarily disabled for the unlocked Debian input test,
then restored from its saved values. The earlier lock-screen screenshot is
diagnostic evidence only, not shortcut acceptance. Physical GNOME screenshots use
the built-in Shift+Print shortcut; the old gnome-screenshot Shell API no longer
works on this GNOME version.

One-shot EFI BootNext selections are used rather than changing BootOrder. The
original Ventoy configuration is backed up as
`ventoy/ventoy.json.before-physical-tests-20261029`. The selection and timeout must
be restored when the physical run finishes.

## Regression evidence and artifacts

- Linux suite: 97 passed.
- Private D-Bus lifecycle suite: 4 passed, including activation, native KDE release
  during recording, restoration and unregister, plus the original portal cases.
- DEB/AppImage packaging fixture: passed for linux-x64, including desktop identity
  validation and the ICU 78 dependency check.
- Actual Debian fixed-package installation: passed.
- Native Windows/macOS runtime acceptance and account sign-in were not performed.

The fixed diagnostic DEB is
`Everywhere-Linux-x64-v0.9.0-canary.20261029.1.deb`. This is a local test artifact,
not a published release. Local builds without release cloud configuration do not
enable sign-in; this was outside the desktop compatibility test.

Local logs, TRX files and screenshots are under the sibling
`ventoy-desktop-lab/physical-evidence` directory. USB artifacts and physical system
logs are retained under `CompatibilityLab/PhysicalTest` and each system's
`~/physical-tests`. No release, PR update or push was performed.