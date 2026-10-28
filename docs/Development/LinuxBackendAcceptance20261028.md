# Linux backend code acceptance, 2026-10-28

## Result and scope

Code review and focused corrections are complete. This is a conditional code-level
acceptance, not a claim that every Linux desktop or Windows/macOS runtime has
passed acceptance. The full Core suite remains failing.

- Reviewed branch: `feat/linux-desktop-backends`.
- Starting HEAD: `b68a986566fdd5b41a7baaf7e294f4260a41e3b2`.
- Upstream baseline: `e963abbe4ff993ff5ba24321c7352df43a9f8370` (`origin/main`),
  checked against the remote at the start of this run.
- Initial index and worktree were clean. The branch and existing history were
  retained; corrections are committed separately without pushing.
- Followed `AGENTS.md`, `.editorconfig`, the Avalonia View/Presentation reference,
  the desktop contracts, platform implementations and their shared callers.

The review covered desktop selection/composition, capability failures, shortcut
ownership, overlay/input lifetime, accessibility/native reference ownership,
capture/coordinate contracts, session identity, shared UI changes and packaging.
The existing separation between Linux-common services and compositor backends is
retained. No new production abstraction, test hook or package dependency was added.

## Corrections

1. A GlobalShortcuts permission request could wait indefinitely while the only
   worker was blocked awaiting its response. Removing a registration or starting
   recording therefore could not close that pending session. The worker now races
   approval against its existing desired-state channel, cancels stale requests,
   and restores an identical desired set after recording. It also cancels the
   unused channel wait when the portal responds first. Denial still does not cause
   an automatic permission-dialog or Host-restart loop.
2. Linux-specific destination scaling, hidden-surface size handling, client-size
   persistence, Chat resize/measurement handling, and blocking Secret Service token
   writes are now confined to Linux. Windows/macOS keep their original paths for
   these operations. Transparent toolbar preparation is also Linux-only; other
   platforms retain visible content during their existing off-screen preparation.
3. Desktop-layer and X11 documentation now describe the portal keyboard backend
   correctly. The explicit private-bus regression fixture and its invocation are
   documented in `LinuxDesktopShortcutValidation.md`.

No Windows or macOS implementation files were modified. Shared contracts still
contain the pre-existing feature-branch changes; this review does not claim that
all shared behavior is identical to upstream. For both WINDOWS and MACOS symbols,
Chat's `OnResized` and `MeasureOverride` method tokens match upstream main after
these corrections. All four changed shared C# files parse under LINUX, WINDOWS and
MACOS symbols. Syntax/token checks are not native platform builds or UI tests.

## Verification

Tests used an isolated Linux ARM64 container and a separate source copy, with no
installed application or desktop configuration changes. Available SDK: 10.0.403;
the copy alone allowed latest-patch roll-forward from the repository's 10.0.401.
The repository's `global.json` was not changed. Runtime: .NET 10.0.14. Account
endpoint/client configuration was absent, so account login was disabled as expected.

| Suite | Baseline passed / failed | Final passed / failed | Not executed in final |
| --- | --- | --- | --- |
| Linux default | 97 / 0 | 97 / 0 | 3 explicit private-bus cases |
| Portal private-bus regression | 0 / 2 | 3 / 0 | 0 |
| Automation | 152 / 0 | 152 / 0 | 0 |
| ProcessIsolation | 68 / 0 | 68 / 0 | 0 |
| Core | 641 / 7 | 645 / 3 | 10 explicit cases |
| Release notification Python | 7 / 0 | 7 / 0 | 0 |

The two baseline portal cases reached BindShortcuts, then timed out waiting for
Close after recording/removal. Both pass with the correction; the third final
case also verifies immediate disposal of the recording lease. These use actual
D-Bus transport and a synthetic portal on a private bus, not a desktop permission UI.

Final Core failures:

- `ReadText_WhenCompositeCrossesMemberBoundary_ContinuesThroughObservedFallback`:
  newline pagination expectation mismatch, also present before correction.
- `InitializeAsync_TavilyApiKeyChanges_PersistsSelectionAcrossReload`: the baseline
  container lacked libsecret; after installing it in the disposable container,
  the test still cannot autolaunch a Secret Service session without DISPLAY.
- `AccountAction_WithoutWindow_UsesApplicationLauncher` for GetSupportCommand:
  support-URI expectation mismatch without account endpoint configuration, also
  present before correction.

Three Input Host RPC tests timed out in the baseline and passed in the final run;
a skill-file watcher also failed only in the baseline. Their varying results do
not prove those races fixed. `RpcConnection.cs` still matches upstream main and
was not modified. No Core failure was concealed or repaired outside this scope.

Linux Release compilation, shell syntax checks, the three Linux workflow actionlint
checks, and CRLF-aware diff whitespace checks passed. Package installation and
AppImage/DEB execution were not rerun; shell syntax is not package acceptance.

## Remaining acceptance boundaries

- No native Windows/macOS build or runtime acceptance in this run.
- No new real-compositor key, permission-dialog, IME, mixed-DPI/multi-monitor,
  accessibility-provider or capture acceptance. Existing historical desktop
  evidence is not promoted to validation of this commit.
- Plasma/GNOME/Niri ordinary Wayland still lack global observation, selection
  overlays and silent capture. Niri requires its external compositor shortcut.
- X11 remains a single-X-screen, scale-1 starter. Hyprland stacking is explicitly
  a focus-history estimate and its input bridge depends on pinned Avalonia internals.
- Exact pinned-SDK verification, physical GPUs and authenticated cloud login remain
  unverified. The Core test failures prevent an unconditional all-tests-passing result.

Raw baseline/final TRX files, logs and platform syntax-check sources are retained
in the sibling `everywhere-acceptance-evidence-20261028` directory.
