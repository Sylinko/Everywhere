# Windows installation and Hosts control

This document describes the implemented Windows service-mode and installer behavior, followed by the validation and installer work that remains. Runtime role and RPC behavior is documented in [Runtime architecture](RuntimeArchitecture.md) and [RPC and Automation Host](RpcAndAutomation.md).

## Product policy

- Main uses `asInvoker` with `uiAccess=false`. Service mode elevates only Input Host and Automation Host; Main does not attempt to acquire UIAccess dynamically.
- Service mode is an explicit installed capability. A configured task remains the launch route even when UAC is disabled or Main already has a full administrative token.
- Inno Setup 7.1 x64 remains the installer. It elevates explicitly and registers an all-users installation.
- Setup EXE and portable ZIP remain supported release forms. Portable and Scoop copies launch ordinary Hosts until the user explicitly enables service mode.
- Task Scheduler is an optional capability. Failure to create or start the task must not invalidate an otherwise usable installation.
- Setup accepts custom directories without changing their ACLs or showing capability warnings. Only a first installation under the system x64 Program Files directory attempts automatic service registration, and only when no Hosts task exists. Normal updates preserve the configured state when the previous uninstaller supports upgrade preservation; historical resets are accepted.
- macOS does not expose this setting and always launches Hosts directly.

“Install as a service” is product language for installing the scheduled elevated launcher. Everywhere does not install a Windows Service, and the controller does not remain resident.

## Controller commands

The executable handles `--hosts-control` before Entrance, DI, Avalonia, or user data initialization.

| Command | Behavior |
| --- | --- |
| `start` | Ask the owned scheduled task to run in the caller's interactive session; directly launch both Hosts if that request fails |
| `launch` | Start exactly the Input and Automation roles with the controller's current token |
| `stop` | Ask the session-local Main instance to stop both roles and verify endpoint disappearance |
| `shutdown` | Windows only: ask Main to exit normally, retain its process handle, wait for termination, and verify that application endpoints are absent |
| `install` | Idempotently create or repair the task for the current executable |
| `uninstall` | Remove the task only when its action belongs to the current executable |

`install` alone accepts `--replace-existing`, which authorizes replacement after Main has shown the task ownership or rebuild confirmation. Installed and portable copies use the same registration path; no environment authorization is required. The controller never accepts an arbitrary executable path or arbitrary child-process arguments.

The stable exit codes are:

| Code | Meaning |
| --- | --- |
| 0 | requested operation completed |
| 1 | operation failed |
| 2 | operation was unavailable or timed out |
| 3 | task replacement confirmation is required |
| 10 | service launch failed; ordinary fallback started with reduced capability |
| 11 | service launch failed; direct fallback is capability-equivalent because the controller already has an administrative token |

CLI stdout and stderr are readable English diagnostics. They are not parsed into persistent UI messages. Structured output remains unnecessary until a real external consumer exists.

### Cooperative application shutdown

Setup runs the **installed** `Everywhere.exe --hosts-control shutdown` before invoking
the previous uninstaller. The installed executable is required because the control
endpoint validates the same build and executable path. `stop` remains a Host-generation
operation and must not be treated as proof that Main has exited.

The shutdown RPC carries no caller-selected PID or path. Main accepts it only when an
application shutdown owner is attached. Its response means acceptance, not completion.
The server drains the response and disposes the control connection before dispatching
normal UI shutdown. The existing application lifetime then cancels initialization,
drains activation, and disposes DI, including the Hosts and Watchdog owners. The RPC
handler never waits for the application lifetime that will dispose that same handler.

The controller retains the authenticated Main process handle **before** sending the
request, waits for that process to terminate, and checks the activation, Main-control,
Input, and Automation endpoints. It has a 30-second deadline. An inaccessible endpoint
is not considered absent. A missing control pipe is successful only when all application
endpoints are absent; it is not itself evidence that Main has exited.

Setup writes ShutdownProtocolVersion=1 and sends shutdown only to installations
advertising this protocol. The request uses the desktop identity when available.
/NOCLOSEAPPLICATIONS skips it. No WMI process-exit gate or directory write probe is
used; native uninstall and file replacement handle actual file-in-use errors.

Opening Setup leaves Main running. On Install, Setup requests shutdown and attempts
old uninstall, including during normal upgrades. Interactive Setup hides its wizard
and uses /SILENT to show the old uninstaller's native progress without startup or
completion confirmation. Silent Setup uses /VERYSILENT and shows no uninstall window.
The wizard and its previous Cancel-button state are restored after each attempt,
before payload installation or Abort/Retry/Ignore. Missing uninstallers never hide it.

During old uninstall, Setup's Cancel button is disabled. Native Inno removal cannot
be gracefully cancelled once it begins; Setup adds no forced stop, automatic timeout,
or kill-on-close job for the uninstaller. It waits for the original process, whose
native Inno lifetime includes actual uninstall work. The temporary self-copy may
still perform its own final cleanup after that process exits. A hanging uninstaller
requires normal system-level intervention. Shutdown controllers retain a 35-second
limit and optional service registration a 10-second limit, with controller-only
kill-on-close jobs. No old-version rollback is promised.

New uninstallers accept /UPGRADE for same-directory replacement: shutdown still runs,
but startup entries, service tasks and shortcuts are preserved. Shortcuts are excluded
from automatic uninstall deletion and removed explicitly on final uninstall. Final
uninstall checks executable ownership before deleting startup values. Historical
uninstallers do not understand /UPGRADE; their state resets, including historical
cross-user startup cleanup, are explicitly accepted without snapshot/restore logic.

## Task Scheduler integration

Windows uses the Task Scheduler COM API through CsWin32. The root task `\Everywhere Hosts` has:

- one fixed action pointing to the owning Everywhere executable;
- arguments `--hosts-control launch` and the executable directory as working directory;
- the built-in Administrators group principal with `HighestAvailable`;
- on-demand execution, `IgnoreNew`, and a one-minute launcher execution limit;
- a protected task DACL granting SYSTEM and Administrators full control and ordinary Users read/run access.

Before requesting UAC, Main asks Task Scheduler to parse the generated XML into a task definition. The elevated controller then registers it with `TASK_LOGON_GROUP`. `start` validates task ownership and calls `IRegisteredTask::RunEx` with `TASK_RUN_AS_SELF | TASK_RUN_USE_SESSION_ID` and the caller's session ID. The returned engine PID is useful only as launch diagnostics; it is not a Host PID or readiness signal.

Task status reads the registered XML action and classifies it as:

- not configured;
- owned by the current executable;
- owned by another Everywhere copy;
- present but invalid or unreadable;
- unavailable because Task Scheduler could not be queried.

Main requests service mode for a new Host generation only when the state is `CurrentExecutable`. The status is read again for every generation, so installing or removing the task takes effect after the coordinated generation restart without a persisted mode preference.

Another copy's task is never replaced or deleted implicitly. Main displays the configured owner and requests confirmation; the elevated controller independently requires `--replace-existing`. Removal follows the same ownership rule. Installation and removal also attempt ownership-aware cleanup of the former elevated-Main task.

## Launch and fallback

The scheduled action runs the short-lived `launch` command. That command starts both role processes and exits; Main continues to own connection, recovery, and shutdown.

If Task Scheduler explicitly rejects the launch, `start` launches both Hosts directly. The controller classifies the fallback from its effective token. Main shows a degraded state only when the fallback has reduced capability. A directly launched Host is healthy when direct mode was intentional or its token is capability-equivalent.

The coordinator does not infer service-mode intent from token shape. With UAC disabled, processes may already receive full administrative tokens and direct fallback can be capability-equivalent, but an owned scheduled task is still started when the user enabled the feature. This keeps behavior and diagnostics stable across UAC policy changes.

If Task Scheduler accepts the launch but a Host never authenticates, Main uses the normal bounded connection and recovery policy and exposes the role failure. It does not start a second candidate merely because the readiness deadline expired; an accepted task can still launch late, and the current shared endpoints do not encode privilege mode.

## Settings and user-facing status

Windows settings contains a System interaction group with:

- one full-width function-status item;
- independent Input and Automation rows;
- one shared Retry connection action;
- one standard “Install as a service” switch.

Each Host row is derived from that role's authenticated connection and recovery state. Starting uses a neutral progress presentation, connected uses green success, reduced-capability fallback uses orange warning, and unavailable uses red error. Status text is dynamically localized; explanatory tooltips identify the affected role without exposing raw exception data. The indexed state templates are created lazily.

The switch reflects actual task ownership rather than a stored Boolean. Enabling it validates the task definition, requests confirmation when replacing another copy or an unidentified task, then invokes the elevated controller. Disabling it removes only the current executable's task. Cancelling a dialog or UAC restores queried state without an error toast. A successful change restarts the Host generation; duplicate operations and retries are disabled while active.

## Installation identity and service-mode boundary

Setup retains layout version 2 and shutdown protocol metadata for installation discovery
and upgrade routing. Service registration does not depend on this identity. Installed,
portable, and Scoop copies can all request service mode through the application setting.

The deployment environment is responsible for installation-directory permissions and
redirection. Neither Setup nor Main audits directory ACLs, owners, ancestors, reparse
points, or writability before service registration. System Program Files is the explicit
administrator-managed location assumption for first-install automatic registration;
its path is not treated as a proof of filesystem security.

Main asks for confirmation only when replacing another copy's task or rebuilding an
unidentified task, then requests elevation. Task modification permissions, fixed command
arguments, and executable ownership remain application responsibilities. Host peer
identity checks and pipe access controls are unchanged.

Successful uninstall need not leave an empty directory. Unknown residual files are
preserved without filename-specific handling. Ignoring uninstall failure allows
overwrite installation and can leave old payload files. Migration does not clear the
old directory; service mode must be enabled explicitly at the new location.

## Host pipe security

Elevated Hosts create a current-user pipe DACL and apply a medium mandatory integrity label. This permits the same user's filtered ordinary Main process to connect while blocking low-integrity writers. OS-derived peer process identity is verified before the role/build/session handshake.

Task action path, working directory, task modification permissions, executable directory protection, and privileged code-loading paths are all part of the boundary. Diagnostic endpoint overrides remain a development surface and require separate review before production elevated use.

## Installer behavior

The Inno Setup 7.1 x64 installer keeps the migration flow small:

- requests administrative installation and writes machine registration;
- preserves the released AppId and uses the modern light/dark wizard;
- hides directory selection only when every discovered previous installation is layout 2, retaining its registered path;
- shows directory selection for a first installation or a legacy layout, defaulting to the system Program Files directory unless an existing layout-2 path is available;
- discovers the current desktop user's legacy installation through the shell process's SID, including alternate-credential elevation when that context is accessible;
- accepts a nonempty recognized old directory without confirmation;
- asks one ordinary Yes/No question for another nonempty directory, disabling Inno's redundant directory-exists warning;
- attempts old uninstall during normal upgrades as well as migration, deduplicating shared payloads; interactive attempts display native uninstall progress while Setup is hidden;
- offers Abort/Retry/Ignore when uninstall is missing, cannot start, or returns failure;
- waits for the native uninstall process without requiring an empty directory or offering forced cancellation;
- preserves unknown residual files;
- preserves startup/service state and shortcuts when the old uninstaller supports /UPGRADE;
- cleans retired shortcuts and user registration only after confirmed uninstall success;
- does not preemptively clean startup entries or scheduled tasks during Setup;
- uses a normalized, case-insensitive, separator-bounded comparison against the system x64 Program Files directory for first-install automatic service registration;
- preserves any existing Hosts task during automatic registration and never passes --replace-existing; optional registration failures are logged;
- enables Setup/uninstall logging and signs both the Setup executable and generated uninstaller;
- retains a global Setup mutex and registers previous Main/Watchdog images with Restart Manager;
- offers a completion-page launch checkbox and starts the app with the current desktop shell's token and environment, rather than Setup's elevated identity;
- omits post-install launch when that identity is unavailable, installation is silent, or a restart is required.

Setup never authorizes task takeover. New same-directory upgrade uninstall preserves
an owned service task or its absence. Older uninstallers can reset it; an upgrade is
not reclassified as first install to re-enable service mode.

Release automation downloads the pinned official Inno Setup 7.1.0 x64 asset and verifies
its GitHub artifact attestation before compiling. Local packaging discovers the compiler
under Program Files or uses INNO_SETUP_COMPILER. Existing release signing remains unchanged.

## Upgrade sequence and failure policy

1. Discover previous paths/layouts without closing the application.
2. Keep the layout-2 upgrade directory fixed; directory selection remains for first installation and legacy migration.
3. On Install, request cooperative shutdown and attempt old uninstall. Same-directory attempts pass /UPGRADE.
4. On failure, retry, ignore and overwrite, or abort further installation. Missing uninstallers have the same recovery choices. Residual files are not failure.
5. Silent or message-suppressed failure exits with code 7 unless /IGNOREUNINSTALLFAILURE explicitly authorizes continuing. No interactive retry loop is opened.
6. Install files and write layout metadata. Only first installation under system x64 Program Files with no Hosts task attempts automatic registration.
7. Offer launch as the desktop user after success.

Ignored failure is not successful retirement: old shortcuts and discovered user
registration are not explicitly deleted. Same-scope registration may still be replaced
by Inno's new registration. Actual write failures can prevent installation. Abort exits
with code 2, leaving completed cleanup as-is. No transaction spans uninstall and
installation; no historical preference backup/restore compatibility is implemented.

## Remaining validation and work

Historical validation before the service-mode simplification used an isolated installer
fixture with its own AppId and exercised missing uninstallers,
nonzero uninstall exit codes, residual files, layout-1 migration, and a real Inno
uninstaller. All continued successfully and preserved unrelated data. Native wizard
checks covered both answers to the nonempty-directory question, the completion
launch checkbox, and the former protected-versus-writable directory assessment.
These historical results do not validate the current uninstall-first upgrade policy. Cancelling a
hanging uninstaller stopped its child processes without entering rollback in the former forced-stop implementation; this behavior is no longer provided.

The service-mode simplification was checked separately:

- The Windows project built successfully with eight Watchdog AOT/trim warnings.
- The production Inno script compiled successfully without emitting or signing an installer.
- All 48 `ProcessRoleAndRpcTests` passed, including rejection of the removed portable authorization option.
- An isolated native Inno probe executed extracted production preparation/routing code and the Program Files guard: 25 assertions covered first install, in-place update, migrations, duplicate registrations, repeat preparation, and path normalization/boundaries. Registry deletion, uninstall execution, and task cleanup were stubbed; this is branch validation, not an end-to-end installer test.
- A separate native Inno probe executed the production automatic-registration eligibility function against the real Task Scheduler in read-only mode. It passed outside the sandbox; the sandbox could not access Task Scheduler COM.

The uninstall-first refactor was checked separately with extracted production code:

- The production Inno script compiles without emitting or signing a package.
- A native policy probe passed 17 assertions covering uninstall success, retry, ignore, cancellation, missing uninstallers, silent failure/explicit ignore, duplicate payloads and retirement metadata. External effects and dialog choices are stubbed.
- A native state probe passed 7 assertions for upgrade preservation, final cleanup, and old/new/empty directory confirmation, with external effects stubbed.
- Before the native-progress simplification, a process probe passed 6 scenarios for successful and failed exit, descendant waiting, forced stop/retry and controller timeout. Its uninstall process-tree and forced-stop assertions describe the former implementation and do not validate the current UX.

The native-progress simplification was checked separately:

- The production script compiled successfully without emitting or signing a package, and the focused diff passed whitespace checks.
- An isolated probe passed 45 assertions using extracted production uninstall/preparation code. Uninstall execution, dialogs and registry/shortcut changes were stubbed; assertions cover mode selection, disabled cancellation while waiting, restoration before retry/ignore/abort, missing uninstallers, silent failure/explicit ignore and upgrade routing.
- A real isolated Inno uninstaller passed interactive and silent handoff checks: startup/completion prompts were skipped, progress-window visibility matched the selected mode, Setup waited for a delayed uninstall action and payload deletion, and its wizard and Cancel state were restored.
- A native launch-failure probe confirmed that Setup restores the wizard and Cancel state when the existing executable cannot be launched and failure is ignored.
- The fixture used a unique AppId, an isolated output directory and no uninstall registry registration. No Everywhere installation or scheduled task was modified.

Real task creation/deletion, complete install/upgrade/uninstall flows, the application confirmation dialogs, alternate-credential uninstall launch, foreground/position transitions, and UAC cancellation still require native acceptance testing. No real application installation or task was modified by these probes.

The following items are still open and must not be described as complete:

### Task and session validation

- Smoke-test task status, registration, `RunEx`, and deletion from a trimmed published build; built-in COM interop still emits trim-compatibility warnings.
- Validate split-token administrators, true standard users, alternate installation credentials, and concurrent RDP sessions.
- Confirm the task DACL permits the filtered caller to run the task while preventing modification.
- Confirm `TASK_RUN_USE_SESSION_ID` selects the intended interactive identity for every supported account shape.

### Installation-wide shutdown

The shutdown controller is scoped to the caller's user/session and exact build. Setup
does not cooperatively stop all logged-on sessions or block on a separate process scan. Alternate-credential
elevation may therefore require manual exit. Native checks must cover elevated Main,
standard-user Main and concurrent RDP sessions.

Setup does not hold a replacement lock honored
by every application launch. A new process starting after shutdown remains a race.
Restart Manager remains additional protection, not proof
of an installation-wide exclusion interval. Legacy installs without the shutdown marker
also require manual exit; no cross-version relaxation of the runtime RPC handshake is made.

Setup resolves the current desktop shell token/SID instead of relying only on elevated HKCU. Alternate-credential elevation, unavailable Explorer, and concurrent RDP sessions still require native acceptance checks.

### Multiple installations and late candidates

Runtime endpoint names are user/session scoped but do not include installation identity. After an explicitly approved task takeover, independent installed and portable copies in the same session can still contend for the same role endpoints. An accepted scheduled launch may also arrive after Main has reported a startup timeout. Keep the present endpoint-ownership behavior until a reproduced failure justifies mode-specific endpoints or a handover protocol.

### Native platform verification

macOS peer verification and direct Host lifecycle must be exercised on a real macOS build. Windows cannot validate the native Unix-socket and `proc_pidpath` behavior. Linux remains on its existing direct-launch path and has not adopted the new platform peer verifier described here.

## References

- [Task Scheduler security contexts](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)
- [IRegisteredTask::RunEx](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex)
- [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
- [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)
- [Windows kernel-object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces)
- [Inno uninstall exit codes](https://jrsoftware.org/ishelp/topic_uninstexitcodes.htm)
- [Inno uninstall command-line options](https://jrsoftware.org/ishelp/topic_uninstcmdline.htm)
- [Launching an unelevated process through Explorer](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643)
- [Windows file security and access rights](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights)
