# Linux terminal updates

## User flow

The existing Common settings update control retains its original icon, title and
check behavior. Clicking an available Linux update opens a chat-style dialog with
the same 44-pixel title bar, 36-pixel Evy avatar and Markdown presentation used by the
chat window. The body has no bubbles. Its localized description and command are appended
to a stable ObservableStringBuilder in up to eight Unicode-safe chunks at 70 ms
intervals. The first chunk appears immediately; nominal completion is about 490 ms.
This preserves real incremental rendering while making the reveal extremely fast.
Copying is disabled until the final chunk, and Cancel immediately stops the timer.

The download sentence is rendered separately using localized text and a hyperlink
control. Only "official website" is linked in English and "官网下载" in Simplified
Chinese. The URL is never included in the streamed Markdown source, so partial link
syntax cannot appear while opening. The complete blue link appears after streaming
finishes. The code block keeps its chat copy action and
success feedback, with the text-wrap toggle hidden in this dialog only. A bottom-right
Cancel button replaces the header X; Escape also cancels. Opening, rendering or
canceling does not modify the clipboard. Detachment stops the timer, releases
localization subscriptions and restores the host's temporary appearance class.

The chat toolbar first cloaks the chat window using its normal close-message path,
then activates the main window and opens this dialog. This preserves pin settings
and prevents a pinned chat window from covering the guidance. The graphical Linux
installer is never invoked. Windows and macOS keep their existing update behavior.

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

The application release authority is the official `Sylinko/Everywhere` repository.
The installer selects published payloads from that repository, independently of
package repositories. The launcher does not inject the application discovery
repository into script arguments.
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

## Independent installer

The canonical source is included under `tools/linux-installer` for independent maintenance. Its exporter copies the bundled snapshot and tests into this checkout.
The launcher passes the `update` action; the same engine also supports clean
installation with an English arrow-key channel menu. Both menu and native package
confirmation use the controlling terminal when launched through `curl | sh`.

The engine checks a separately versioned installer manifest before system routing.
Verified new code runs from a private user cache, without changing package-owned
files. The manifest and script are maintained in the official repository directory;
no installer Release is required. Network failure falls back visibly to the bundled
engine. Application downloads come from official releases. An explicit `--catalog`
can select a schema-1 website catalog instead.

- `ew update --dry-run`: preview without downloading application packages.
- `ew update --channel stable`: override the channel for this invocation.
- `ew update --catalog https://HOST/releases.json`: use an explicit website catalog.
- `ew update --no-installer-update`: use the bundled script directly.
- `ew update --proxy none`: override saved network behavior for this invocation.

Arch downloads the selected official archive and makes a local native package. It uses
`pacman -U`, never an AUR helper or a full system upgrade. Unsupported systems and
missing assets fail explicitly. The independent installer's README documents the
manifest protocol, initial backend scope and offline staging inputs.

## Packaging and limits

DEB, RPM and the downstream AUR reference template install the `ew` alias
and declare Python dependencies. The bundled script is included by the Linux project.
The downstream AUR maintainer must adopt the updated template. The release workflow builds DEB, RPM and tar.zst assets. Existing public releases
may still lack RPM assets until this workflow is published and a new tag is built.

The script validates installed ownership, architecture, release URL, SHA256, size,
package metadata and the installed version after the transaction. It does not bypass
signature checks, force dependency removal, or silently downgrade. Missing assets or
unsupported sources fail explicitly. NixOS, immutable systems, Alpine/musl and portable
installs require their own installation workflow. openSUSE is not yet an enabled
installer backend; its package dependencies and runtime need separate validation.

## Validation

`tests/linux-update` exercises package commands using fake executables in an isolated
PATH/HOME. Core tests cover discovery-only policy, the real settings control, localization,
inline clipboard copying, close behavior and the chat-to-main-window update
request. Linux tests cover release selection,
settings precedence, read-only settings loading and the existing proxy behavior.

Set `EVERYWHERE_UPDATE_PREVIEW_DIR` while running `TerminalUpdatePresentationTests` to
render the real dialog in light/dark themes and Chinese/English with Avalonia/Skia.
Version 99.0.0-canary.1 is fixture data. Headless renders and message-routing tests do
not represent physical-desktop activation or Linux package installation acceptance.

The installer checks `tools/linux-installer/installer.json` on official main through
raw.githubusercontent.com. The manifest and script are committed together; no
installer GitHub Release is required. Increment `INSTALLER_VERSION` and regenerate
the manifest with `build-manifest.py` when changing the distributed installer.
