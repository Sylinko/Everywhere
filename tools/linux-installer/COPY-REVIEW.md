# Installer copy review — 0.3.0

English text is listed exactly as owned by the installer. Braces mark runtime values.
Native package-manager messages, operating-system exception details and argparse-generated
usage/invalid-argument syntax are supplied by those components, not fixed installer copy.

## Normal interface

| ID | Key | English |
| --- | --- | --- |
| U01 | title | Everywhere |
| U02 | tagline | Context-aware Desktop AI Assistant |
| U03 | channel | Release channel |
| U04 | channel_compact | Channel |
| U05 | stable | Stable |
| U06 | stable_desc | Recommended · Official release |
| U07 | canary | Canary |
| U08 | canary_desc | Preview · Experimental build |
| U09 | keys | ↑/↓ Navigate · Enter Select · Esc Cancel |
| U10 | keys_compact | ↑↓  Enter  Esc |
| U11 | system_label | System |
| U12 | desktop_label | Desktop |
| U13 | checking_installer | Checking installer |
| U14 | bundled_installer | Using bundled installer (check unavailable). |
| U15 | using_installer | Installer {version} |
| U16 | checking_release | Checking releases |
| U17 | target | {version} · {channel} |
| U18 | download | Download |
| U19 | retry | Retrying download ({attempt}/3) |
| U20 | verify | Verifying |
| U21 | prepare | Preparing package |
| U22 | install | Installing |
| U23 | installed | Installed {version} |
| U24 | updated | Updated to {version} |
| U25 | open | Open Everywhere from your app menu, or run: Everywhere |
| U26 | launch_update | Run anytime in terminal: ew update |
| U27 | hyprland_tip | Tip: Install grim and wl-clipboard for screenshot & selection support. |
| U28 | restart | Restart Everywhere. |
| U29 | current | Already up to date. |
| U30 | older | No newer version in this channel. |
| U31 | preview | Preview complete. |
| U32 | failed | Installation failed: {error} |
| U33 | canceled | Canceled. |

Progress data: percentage; received/total bytes; average speed; estimated remaining time.
Units: `B`, `KiB`, `MiB`, `GiB`, `/s`. Unknown remaining time: `--:--`.
Native stages keep their normal interactive confirmations.

## Installer errors

| ID | English |
| --- | --- |
| E01 | Only Linux x86_64 and aarch64 assets are published. |
| E02 | Refusing a non-HTTPS redirect. |
| E03 | Download timed out. |
| E04 | Download exceeds its size limit. |
| E05 | Download failed using the application network settings. |
| E06 | Only HTTPS downloads are allowed. |
| E07 | Invalid application download transport. |
| E08 | Application transport returned an invalid response. |
| E09 | Response exceeds expected size limit. |
| E10 | GitHub API rate limit exceeded. Use the default catalog or set GITHUB_TOKEN. |
| E11 | Download HTTP {exc.code}: {url} |
| E12 | Download failed: {exc} |
| E13 | Expected a version string. |
| E14 | Unsupported version: {value} |
| E15 | Manifest must be an HTTPS URL or an explicit absolute file path. |
| E16 | Invalid download metadata. |
| E17 | Download size or SHA256 mismatch. |
| E18 | Unsupported installer protocol. |
| E19 | Installer is too large. |
| E20 | Installer cache must be private and owned by the current user. |
| E21 | Unsafe installer cache entry. |
| E22 | Choose --channel stable or --channel canary in a terminal. |
| E23 | This immutable system is not supported. |
| E24 | Unsupported system: {raw_distro} {raw_version}. |
| E25 | Required system tool is missing: {manager} |
| E26 | Unexpected installed package identity. |
| E27 | Multiple Everywhere packages are installed. |
| E28 | Invalid release repository. |
| E29 | Invalid release response. |
| E30 | Release history exceeds the complete scan limit. |
| E31 | Expected a release object. |
| E32 | Unsupported download catalog. |
| E33 | Expected release channels. |
| E34 | No Linux release is available for {channel}. |
| E35 | Expected a channel release object. |
| E36 | Release channel and version disagree. |
| E37 | Expected release assets by architecture. |
| E38 | No supported Linux download is available for this system. |
| E39 | Mirror must be official, github, or an HTTPS URL. |
| E40 | Run the installer as a normal user, without sudo. |
| E41 | Arch packaging requires makepkg and zstd. |
| E42 | Unsafe archive path. |
| E43 | Unsupported archive entry; only regular files and directories are allowed. |
| E44 | Expanded archive is too large. |
| E45 | Archive decompression failed. |
| E46 | The download is not an Everywhere application archive. |
| E47 | Arch packaging did not produce exactly one package. |
| E48 | Package identity/version/architecture differs from download metadata. |
| E49 | sudo is required for the package transaction. |
| E50 | Updater cache directory has unsafe ownership or permissions. |
| E51 | Another updater is already running. |
| E52 | This installer supports Linux only. |
| E53 | Use HTTPS_PROXY for standalone installation; --proxy requires ew update. |
| E54 | Everywhere is not installed. Run the installer without update first. |
| E55 | Packaging resource is too large. |
| E56 | The release is missing required packaging resources. |
| E57 | Missing packaging resource: {label} |
| E58 | Installation changed while downloading; please retry. |
| E59 | Installation completed without the expected version. Check the package manager output. |

Every E message is displayed through `Installation failed: {error}`.

## Bootstrap

| ID | English |
| --- | --- |
| B01 | Unsupported system. This installer supports Linux only. |
| B02 | Python 3.9+ is required to run the Everywhere installer. |
| B03 | Install it first: sudo pacman -S --needed python |
| B04 | Install it first: sudo apt-get update && sudo apt-get install -y python3 |
| B05 | Install it first: sudo dnf install -y python3 |
| B06 | Install python3 (3.9+) using your system package manager, then run this command again. |
| B07 | HTTPS is required. |
| B08 | Download exceeds its size limit. |
| B09 | Unsupported installer protocol. |
| B10 | Invalid installer metadata. |
| B11 | Installer checksum mismatch. |
| B12 | Checking installer |
| B13 | Download |
| B14 | Verifying |
| B15 | Installation failed: {error} |
| B16 | Canceled. |

## CLI help

| ID | Option | English |
| --- | --- | --- |
| H01 | --no-installer-update | use the current verified installer |
| H02 | --mirror | select official, github, or an HTTPS GitHub mirror prefix |
| H03 | --proxy | override application proxy for this invocation |

Program: `Everywhere installer`. Actions: `install`, `update`.
Channel values: `stable`, `canary`, `auto`. The compatibility `--apply` flag is hidden.

## Interface sample

```text
  ╭─────────╮  █▀▀▀ █   █ █▀▀▀ █▀▀▄ █   █ █   █ █  █ █▀▀▀ █▀▀▄ █▀▀▀
  │  █   █  │  █▀▀  ▀▄ ▄▀ █▀▀  █▀▀▄  ▀█▀  █ █ █ █▀▀█ █▀▀  █▀▀▄ █▀▀ 
  │         │  ▀▀▀▀   ▀   ▀▀▀▀ ▀  ▀   ▀    ▀ ▀  ▀  ▀ ▀▀▀▀ ▀  ▀ ▀▀▀▀
  ╰─────────╯  Context-aware Desktop AI Assistant

  System    Arch Linux (x86_64) · pacman
  Desktop   Hyprland (Wayland) · Full integration

╭─ Release channel ──────────────────────────────────╮
│ ❯ Stable   Recommended · Official release          │
│   Canary   Preview · Experimental build            │
╰────────────────────────────────────────────────────╯
↑/↓ Navigate · Enter Select · Esc Cancel

Checking releases
◆ 0.9.0-canary.20261110.31 · Canary
Download  ━━━━━━━━━─────   60%  39.4 / 65.7 MiB  4.1 MiB/s  00:06
• Verifying
• Preparing package
• Installing
✓ Updated to 0.9.0-canary.20261110.31
Restart Everywhere.
```

The sample version and progress values illustrate layout; they are not live download evidence.
