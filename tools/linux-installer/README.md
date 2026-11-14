# Everywhere Linux installer

This directory contains the independently versioned installer source for review. Nothing is published automatically. `installer.py` requires Python 3.9+ and uses only its standard library. This replaces the former `everywhere-linux-updater` experiment; that directory is retired.

The application package includes a generated snapshot of this installer. `ew update` starts it before desktop initialization, passes the saved channel and delegates HTTPS to the existing application proxy transport. The installer checks its own release manifest first, verifies newer code, caches it in a private per-user directory, and runs it with the same arguments. Package-owned files are never rewritten by self-update. Network failure uses the bundled version with a visible message; invalid metadata or a checksum failure stops execution.

## Download sources

The temporary application source is **pasical/Everywhere**. Stable and Canary targets are selected from that repository's published GitHub Releases, including pagination. Package repositories and AUR do not decide the target. Drafts, inconsistent channels and installer-only tags are ignored. The newest release must have the required asset; older releases are not silently substituted. Downloads validate HTTPS, size, SHA256 and native package metadata.

The reserved installer publication URL is:

```
https://github.com/pasical/Everywhere/releases/download/linux-installer-latest/installer.json
```

**This release/asset has not been published.** Until it is published, normal update checks report the failure and use the bundled engine. The generated `install.sh` bootstrap cannot perform an online first installation until an installer manifest and its script have been hosted. No publishing is performed by this project. The application payload source is already live.

The future website can serve `install.sh`, `installer.json` and `releases.json`, without changing the installer protocol. Use `--catalog https://HOST/releases.json` to select a website catalog. There is no HTML scraping. Explicit absolute manifest files are also supported for staging; they are trust inputs, not verification bypasses. `EVERYWHERE_INSTALLER_MANIFEST` configures the shell bootstrap's HTTPS manifest location.

## Usage

```sh
python3 installer.py install
python3 installer.py update --channel canary
python3 installer.py update --dry-run --channel canary
python3 installer.py --no-installer-update --channel stable
```

A clean installation without an explicit channel opens an English Up/Down menu. Enter selects; Esc/Ctrl+C cancels. It reads `/dev/tty`, including through `curl | sh`. Package-manager confirmation also reads the controlling terminal when the original stdin is a pipe. Without a terminal, supply `--channel`; package confirmations are not automatically accepted.

Existing updates use the application's saved channel, or infer the installed build's channel when run standalone. No silent downgrade or same-version reinstall is performed. Disabling the app's automatic checks does not disable an explicit terminal update. An application restart is required to run newly installed code; the installer does not terminate the application.

## Initial backend scope

- Ubuntu 24.04/25.10 and Debian 13/14: local DEB installed with apt, including dependency resolution.
- Fedora 44: local RPM installed with dnf. Published fork releases must actually contain an RPM asset; older tags may not.
- Arch, EndeavourOS and Manjaro: the verified fork tar.zst is repackaged with makepkg and installed with `pacman -U`. No AUR helper, repository-target lookup or full system upgrade. Artwork/license come from release assets or the same fork tag's source files. Only regular files/directories are accepted in the application archive; traversal, symlinks and oversized expansion are rejected.
- Other releases, architectures without artifacts, immutable systems and NixOS fail explicitly. This list is routing support, not native acceptance on every listed release. Ubuntu/Debian/Fedora require separate native transaction acceptance for this installer revision.

The bootstrap reports a missing Python interpreter rather than installing a runtime without permission. Arch packaging requires makepkg, fakeroot and zstd. makepkg resolves missing application dependencies through its normal --syncdeps transaction. Native package managers retain dependency/signature checks and their ordinary confirmation prompts. Installing through this tool can subsequently be superseded by an explicit package-source operation; this tool does not pin system packages or rewrite repository configuration.

## Release protocol

Installer manifest, schema/protocol 1:

```json
{
  "schema": 1,
  "protocol": 1,
  "version": "0.1.0",
  "asset": {
    "url": "https://HOST/install/v0.1.0/installer.py",
    "size": 12345,
    "sha256": "64 lowercase hexadecimal characters"
  }
}
```

The installer version is independent of the application channel. HTTPS publication is the trust root; checksums from that publisher detect inconsistent/corrupted bytes and do not constitute an independent signature.

Website catalog, schema 1:

```json
{
  "schema": 1,
  "channels": {
    "canary": {
      "version": "0.9.0-canary.20261029.30",
      "assets": {
        "x64": {
          "deb": {"url": "https://HOST/application.deb", "size": 12345, "sha256": "..."},
          "rpm": {"url": "https://HOST/application.rpm", "size": 12345, "sha256": "..."},
          "tar.zst": {
            "url": "https://HOST/application.tar.zst", "size": 12345, "sha256": "...",
            "resources": {
              "icon": {"url": "https://HOST/icon.png", "size": 12345, "sha256": "..."},
              "license": {"url": "https://HOST/LICENSE", "size": 12345, "sha256": "..."}
            }
          }
        }
      }
    }
  }
}
```

Stable uses a separate `stable` entry with a stable version. arm64 uses an `arm64` architecture entry. Sizes and hashes above are illustrative; `build-release.py` generates real installer values. The two Arch resources are required for website catalogs. Future backend work belongs here, not in the application launcher.

## Export and verification

```sh
python3 tools/linux-installer/sync-bundle.py .
python3 tools/linux-installer/sync-bundle.py . --check
cd tools/linux-installer
python3 -m unittest discover -s tests -v
sh -n install.sh
python3 build-release.py --base-url https://HOST/install/v0.1.0 --output /tmp/installer-release
```

The exporter touches only the named checkout's bundled Python file and generated installer tests. Run the exporter from the repository root after changing the installer source. The existing Linux proxy-transport tests stay in the application repository.

## Terminal presentation (0.2.0, pending copy review)

The compact channel card uses the terminal's cyan/blue palette, rounded borders,
a highlighted selection and arrow-key navigation. Its visual reference is the
[Dank/Dux TUI styling](https://github.com/duxlabs-dev/dux-shell/blob/master/core/internal/tui/styles.go)
and [choice views](https://github.com/duxlabs-dev/dux-shell/blob/master/core/internal/tui/views_selection.go).
No new runtime package is required.

Downloads show actual received bytes, percentage, average transfer rate and ETA.
Standalone HTTPS reports chunks as they arrive. When running through ew update,
the UI observes the file written by the existing .NET download helper; the helper
protocol and application proxy behavior do not change. Canceling reaps the helper.
The bar adapts to narrow terminals. Redirected output has one completion line per
download; TERM=dumb disables cursor animation and NO_COLOR disables colors.
Native package-manager confirmations remain interactive.

`COPY-REVIEW.md` lists all installer-owned interface/error/bootstrap/help text.
Regenerate it with `python3 review-copy.py` after changing copy. Package-manager
output and OS-generated diagnostic details are owned by those components.

The first pushed application tag v0.9.0-canary.20261110.31 bundles installer 0.1.0.
This 0.2.0 revision is maintained separately and is not published pending manual
copy review. Refresh a future app snapshot explicitly with sync-bundle.py.
