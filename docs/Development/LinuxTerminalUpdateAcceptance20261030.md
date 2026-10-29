# Terminal update acceptance — Fedora, 2026-10-30

## Scope and installed package

The independent `feat/linux-terminal-update` branch includes the RPM workflow from
`main`, with its own updater changes. It is not merged into `main` or
`linux-hyprland`. A self-contained x64 RPM from this branch is installed on Fedora
44, Plasma 6.7.5 Wayland, with SELinux enforcing.

The local test build deliberately uses `0.9.0-canary.20261029.29`, below the public
`v0.9.0-canary.20261029.30` release, to exercise real update discovery. This is a
local branch build, not a rebuild of an upstream tag. The original `.30` package
and settings are retained on the machine for rollback.

## Physical UI and command results

- The real settings control discovers the published `.30` release and displays a
  terminal icon, Copy command, and the release version in its existing layout.
- English/light, English/dark and Simplified Chinese/dark were visually inspected
  at the physical display's 125% scale. The update text fits and remains readable.
- Actual mouse clicks replace a known clipboard marker with exactly `ew update`.
  The success toast displays the localized instruction to paste into a terminal.
  The original clipboard contents were restored after the checks.
- `/usr/bin/ew` resolves to `/opt/Everywhere/Everywhere`. Its help displays
  `usage: ew update`, with no incorrect claim that this entry point defaults to
  preview mode. The installed script and its Python dependency are present.
- `ew update --help`, `ew update --dry-run` and `ew update` run over SSH without a
  graphical environment. The latter two identify the installed RPM/dnf and
  correctly fail with exit 1 because the newest public release lacks an RPM.
  No package transaction occurs and settings file checksums remain unchanged.
- Actual help and dry-run output were also inspected in a native Ghostty window.
  `rpm -V everywhere` reports no altered package files.

Fedora's direct GitHub API connection intermittently fails during TLS setup.
Successful discovery and terminal checks used a temporary localhost SOCKS tunnel
through the application's existing proxy settings. This establishes the proxy
path, not reliability of the machine's direct internet connection.

## Automated checks and boundaries

- 43 bundled Python updater tests pass.
- 11 targeted Linux update tests pass; the broader Linux run executes 108 passing
  tests. Four private-D-Bus scenarios require their separately enabled test bus
  and are outside this run.
- 22 shared updater lifecycle/presentation tests pass, including localized title,
  terminal icon, clipboard command and the success toast.
- x64 and ARM64 DEB/RPM packaging checks pass, including the `ew` alias and
  explicit Python dependency. Shell syntax and Git whitespace checks pass.

The latest public release currently provides DEB/AppImage/tar.zst and no RPM.
A real downloaded RPM upgrade remains to be accepted after publishing a release
with the RPM workflow. No release, upstream branch or AUR repository was changed
by this test. Account login, visual context and non-Linux runtime acceptance are
outside scope.
