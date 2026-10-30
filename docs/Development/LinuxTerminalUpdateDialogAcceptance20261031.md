# Compact update dialog acceptance — Arch, 2026-10-31

The `feat/linux-terminal-update-dialog` variant restores the original update
control's RefreshCw icon and localized Update available title. Clicking it opens
a compact dialog: one instruction, the selectable `ew update` command, an official
download link, Cancel and Copy command. The former tabs and three-step tutorial
are removed. Opening the dialog does not display a false checking indicator.

## Physical acceptance

- Arch Linux, Hyprland Wayland, 2560 × 1600 at 160% scale.
- Local pacman package `everywhere-bin 0.9.0canary.20261029.29-3` contains this
  branch's modified Core assembly, localization and bundled terminal updater.
  Its deliberately older application version discovers the actual published
  `v0.9.0-canary.20261029.30` release.
- Simplified Chinese light and dark dialogs were inspected on the physical
  desktop. Command, description, download link and footer buttons fit without
  scrolling or clipping.
- A real mouse test sets an independent clipboard marker. Opening the dialog
  leaves it unchanged; Cancel also leaves it unchanged. Confirming copies exactly
  `ew update`. The original clipboard contents are restored afterward.
- The package integrity check reports 499 files and zero altered files. The old
  installed package and configuration are retained in the machine's lab rollback
  directory.
- The two presentation tests pass, including original title/icon, dialog content,
  website URI, clipboard behavior and the absence of a false checking state.

The physical machine intermittently fails direct GitHub TLS connections. Actual
release discovery used a temporary localhost SOCKS tunnel, which is removed after
acceptance. No full system upgrade, AUR push, release publication or main-branch
merge is part of this UI test. The installed package remains available for manual
review.
