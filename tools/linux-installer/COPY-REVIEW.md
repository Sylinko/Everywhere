# Installer copy review — 0.2.0

English text is listed exactly as owned by the installer. Braces mark runtime values.
Native package-manager messages, operating-system exception details and argparse-generated
usage/invalid-argument syntax are supplied by those components, not fixed installer copy.

## Normal interface

| ID | Key | English |
| --- | --- | --- |
| U01 | title | Everywhere |
| U02 | channel | Release channel |
| U03 | channel_compact | Channel |
| U04 | stable | Stable |
| U05 | canary | Canary |
| U06 | keys | ↑/↓ Navigate · Enter Select · Esc Cancel |
| U07 | keys_compact | ↑↓  Enter  Esc |
| U08 | checking_installer | Checking installer |
| U09 | bundled_installer | Using bundled installer (check unavailable). |
| U10 | using_installer | Installer {version} |
| U11 | checking_release | Checking releases |
| U12 | target | {version} · {channel} |
| U13 | download | Download |
| U14 | retry | Retrying download ({attempt}/3) |
| U15 | verify | Verifying |
| U16 | prepare | Preparing package |
| U17 | install | Installing |
| U18 | installed | Installed {version} |
| U19 | updated | Updated to {version} |
| U20 | open | Open Everywhere. |
| U21 | restart | Restart Everywhere. |
| U22 | current | Already up to date. |
| U23 | older | No newer version in this channel. |
| U24 | preview | Preview complete. |
| U25 | failed | Installation failed: {error} |
| U26 | canceled | Canceled. |

Progress data: percentage; received/total bytes; average speed; estimated remaining time.
Units: `B`, `KiB`, `MiB`, `GiB`, `/s`. Unknown remaining time: `--:--`.
Native stages keep their normal interactive confirmations.

## Installer errors

| ID | English |
| --- | --- |
| E01 | Only Linux x86_64 and aarch64 assets are published. |
| E02 | Refusing a non-HTTPS redirect. |
| E03 | Download exceeds its size limit. |
| E04 | Download timed out. |
| E05 | Download failed using the application network settings. |
| E06 | Only HTTPS downloads are allowed. |
| E07 | Invalid application download transport. |
| E08 | Application transport returned an invalid response. |
| E09 | Response exceeds expected size limit. |
| E10 | Download HTTP {exc.code}: {url} |
| E11 | Download failed: {exc} |
| E12 | Expected a version string. |
| E13 | Unsupported version: {value} |
| E14 | Manifest must be an HTTPS URL or an explicit absolute file path. |
| E15 | Invalid download metadata. |
| E16 | Download size or SHA256 mismatch. |
| E17 | Unsupported installer protocol. |
| E18 | Installer is too large. |
| E19 | Installer cache must be private and owned by the current user. |
| E20 | Unsafe installer cache entry. |
| E21 | Choose --channel stable or --channel canary in a terminal. |
| E22 | This immutable system is not supported. |
| E23 | Unsupported system: {distro} {version}. |
| E24 | Required system tool is missing: {manager} |
| E25 | Unexpected installed package identity. |
| E26 | Multiple Everywhere packages are installed. |
| E27 | Invalid release repository. |
| E28 | Invalid release response. |
| E29 | Release history exceeds the complete scan limit. |
| E30 | Expected a release object. |
| E31 | Unsupported download catalog. |
| E32 | Expected release channels. |
| E33 | No Linux release is available for {channel}. |
| E34 | Expected a channel release object. |
| E35 | Release channel and version disagree. |
| E36 | Expected release assets by architecture. |
| E37 | No supported Linux download is available for this system. |
| E38 | Run the installer as a normal user, without sudo. |
| E39 | Arch packaging requires makepkg and zstd. |
| E40 | Unsafe archive path. |
| E41 | Unsupported archive entry; only regular files and directories are allowed. |
| E42 | Expanded archive is too large. |
| E43 | Archive decompression failed. |
| E44 | The download is not an Everywhere application archive. |
| E45 | Arch packaging did not produce exactly one package. |
| E46 | Package identity/version/architecture differs from download metadata. |
| E47 | sudo is required for the package transaction. |
| E48 | Updater cache directory has unsafe ownership or permissions. |
| E49 | Another updater is already running. |
| E50 | This installer supports Linux only. |
| E51 | Use HTTPS_PROXY for standalone installation; --proxy requires ew update. |
| E52 | Everywhere is not installed. Run the installer without update first. |
| E53 | Missing packaging resource: {label} |
| E54 | The release is missing required packaging resources. |
| E55 | Packaging resource is too large. |
| E56 | Installation changed while downloading; please retry. |
| E57 | Installation completed without the expected version. Check the package manager output. |

Every E message is displayed through `Installation failed: {error}`.

## Bootstrap

| ID | English |
| --- | --- |
| B01 | Unsupported system. This installer supports Linux only. |
| B02 | Python 3.9+ is required. Install python3 and run this command again. |
| B03 | Python 3.9+ is required. |
| B04 | HTTPS is required. |
| B05 | Download exceeds its size limit. |
| B06 | Unsupported installer protocol. |
| B07 | Invalid installer metadata. |
| B08 | Installer checksum mismatch. |
| B09 | Checking installer |
| B10 | Download |
| B11 | Verifying |
| B12 | Installation failed: {error} |
| B13 | Canceled. |

## CLI help

| ID | Option | English |
| --- | --- | --- |
| H01 | --no-installer-update | use the current verified installer |
| H02 | --proxy | override application proxy for this invocation |

Program: `Everywhere installer`. Actions: `install`, `update`.
Channel values: `stable`, `canary`, `auto`. The compatibility `--apply` flag is hidden.

## Interface sample

```text
 Everywhere

╭─ Release channel ─────────────────────╮
│❯ Stable                              │
│  Canary                              │
╰──────────────────────────────────────╯
↑/↓ Navigate · Enter Select · Esc Cancel

Checking releases
0.9.0-canary.20261110.31 · Canary
Download  ━━━━━━━━━─────   60%  39.4 / 65.7 MiB  4.1 MiB/s  00:06
Verifying
Preparing package
Installing
Updated to 0.9.0-canary.20261110.31
Restart Everywhere.
```

The sample version and progress values illustrate layout; they are not live download evidence.
