# Linux terminal updates

## User flow

The existing Common settings update control retains its layout and check behavior.
When a Linux update is available, its icon becomes a terminal and its title becomes
“Copy command”. Clicking copies exactly `ew update` and shows the existing success
toast with instructions to paste it into a terminal. There is no new card or page.
Windows and macOS keep their existing navigation and installer behavior.

`ew update` reads the same saved `settings.json` as the application. Explicit CLI
options override saved values for that invocation; they never rewrite preferences.

- The selected channel follows `Common.UpdateChannel`. `Unknown` resolves using the
  running application's channel, through the same method used by the GUI updater.
- Application downloads use the existing `DynamicWebProxy`, including enabled/system
  proxy selection, HTTP/HTTPS/SOCKS5, authentication, local bypass and bypass patterns.
- `Common.IsAutomaticUpdateCheckEnabled` controls background discovery, not an explicit
  user-requested update. Disabling it does not disable `ew update`.
- Native package managers still own their own repository, dependency and authentication
  policies. They may request an administrator password or transaction confirmation.

## Implementation

`LinuxUpdateHandler` supplies Linux release discovery via `ITerminalUpdateHandler`.
The shared coordinator keeps notification and check ownership, but does not recover,
automatically download, or graphically install packages for this capability.
The existing changelog history uses the same source; its graphical installer action
is disabled on Linux, without changing its view layout.

The temporary release authority is explicitly `pasical/Everywhere`, shared by discovery
and the CLI launcher. A future official rollout must switch this authority deliberately.
There is no silent official/fork fallback. An incomplete newest Linux release does not
silently select an older version. Native package validation remains in the script.

`Everywhere update` and the `/usr/bin/ew` alias run before desktop, single-instance,
process-role and watchdog initialization. The entry point starts the bundled
`linux-update.py` using Python 3.9+, with inherited terminal streams and the child exit
status. Arguments are passed as an array, never evaluated by a shell.

The application projects only update/proxy settings with the existing settings binder.
This read-only path performs no migration or save. The script calls the same executable's
internal bounded HTTPS download mode so proxy semantics are not reimplemented in Python.
Proxy credentials stay out of command arguments and download diagnostics. The helper
checks every redirect for HTTPS, caps response size, and has a bounded timeout.

## Optional overrides

Ordinary use requires only `ew update`. Advanced options include:

- `ew update --dry-run`: preview without downloading or installing packages.
- `ew update --channel stable`: override the saved channel for this invocation.
- `ew update --version 0.9.0-canary.20261012.26 --channel canary`: require an exact
  published target; downgrade prevention still applies.
- `ew update --proxy system`, `--proxy none`, or `--proxy http://host:port`: override
  saved network policy. An endpoint override replaces saved authentication/bypass rules.

Arch/AUR follows its selected recipe. The script checks the offered package against the
requested channel and refuses a mismatch rather than ignoring settings. A normal
interactive invocation asks permission for the required full system upgrade; no extra
argument is needed. `--allow-system-upgrade` is an explicit consent override for that
step, while package/build review prompts remain interactive.

## Packaging and limits

DEB, RPM and the downstream AUR reference template install the `ew` alias
and declare Python dependencies. The bundled script is included by the Linux project.
The downstream AUR maintainer must adopt the updated template. The release workflow builds DEB, RPM and tar.zst assets. Existing public releases
may still lack RPM assets until this workflow is published and a new tag is built.

The script validates installed ownership, architecture, release URL, SHA256, size,
package metadata and the installed version after the transaction. It does not bypass
signature checks, force dependency removal, or silently downgrade. Missing assets or
unsupported sources fail explicitly. NixOS, immutable systems, Alpine/musl and portable
installs require their own installation workflow. openSUSE RPM dependency compatibility
still requires distribution validation.

## Validation

`tests/linux-update` exercises package commands using fake executables in an isolated
PATH/HOME. Core tests cover discovery-only policy, the real settings control, localization,
clipboard contents and the paste instruction toast. Linux tests cover release selection,
settings precedence, read-only settings loading and the existing proxy behavior.

Set `EVERYWHERE_UPDATE_PREVIEW_DIR` while running `TerminalUpdatePresentationTests` to
render the real settings button in light/dark themes with Avalonia/Skia. Version
99.0.0-canary.1 is fixture data. These are macOS-hosted Avalonia renders; they do not
represent Linux package installation acceptance.
