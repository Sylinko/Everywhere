# Fedora physical acceptance — 2026-10-30

## Environment and package

- Fedora 44, x86_64, kernel 7.2.13-200.fc44.
- GNOME Wayland and Plasma 6.7.5 Wayland on the same physical machine.
- SELinux enforcing throughout application acceptance.
- RPM built from the published `v0.9.0-canary.20261029.30` tar.zst payload.
  The installed executable matches the original payload SHA-256:
  `f7baebd6c6e400f63f8679a338196c1d36df012d425db31a29f5a6e91d94eb66`.

## Results

- DNF resolves dependencies and installs the RPM without bypassing dependency
  checks. `rpm -V everywhere` reports no modified package files.
- Both desktops start the native Avalonia Wayland backend; Input and Automation
  hosts authenticate successfully.
- GNOME's Add Keyboard Shortcuts dialog and Plasma's Global Shortcuts Requested
  dialog grant Ctrl+Shift+E. Physical uinput key sequences then display Chat on
  both desktops. Portal logs confirm one registered shortcut in each session.
- The main window has visible window controls and an opaque frame on both
  desktops. Plasma's tray activation opens the main window and its status
  notifier remains registered.
- Ghostty 1.4.0 and fastfetch 2.68.1 are installed for further manual testing.

The test image initially lacked the SELinux label on `/etc/shadow` and several
physical-device firmware packages. Restoring the original shadow label and
installing the missing GPU, Wi-Fi and audio firmware repaired the test system;
these changes are outside the application repository.

## Limits

Visual context retrieval outside Hyprland is excluded from this acceptance.
Plasma's visual-target warning does not prevent Chat activation. X11, ARM64
hardware, account login and model responses were not exercised in this round.
The existing application's RPM update-execution TODO is unchanged.

Both x64 and ARM64 DEB/RPM packaging fixtures pass, including package identity,
version, architecture, desktop entry and payload checks. Ten release-automation
unit tests pass. These checks do not replace running the GitHub release workflow.
