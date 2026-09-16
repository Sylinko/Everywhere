# Windows installation and Hosts control

This document describes the implemented Windows service-mode and installer behavior, followed by the validation and installer work that remains. Runtime role and RPC behavior is documented in [Runtime architecture](RuntimeArchitecture.md) and [RPC and Automation Host](RpcAndAutomation.md).

## Product policy

- Main normally runs with the user's ordinary token. Service mode elevates only Input Host and Automation Host.
- Service mode is an explicit installed capability. A configured task remains the launch route even when UAC is disabled or Main already has a full administrative token.
- Inno Setup remains the installer. It elevates explicitly and registers an all-users installation.
- Setup EXE and portable ZIP remain supported release forms. Portable and Scoop copies launch ordinary Hosts until the user explicitly enables service mode.
- Task Scheduler is an optional capability. Failure to create or start the task must not invalidate an otherwise usable installation.
- An unprotected custom directory is allowed after a clear warning. The warning does not prohibit service-mode installation.
- macOS does not expose this setting and always launches Hosts directly.

“Install as a service” is product language for installing the scheduled elevated launcher. Everywhere does not install a Windows Service, and the controller does not remain resident.

## Controller commands

The executable handles `--hosts-control` before Entrance, DI, Avalonia, or user data initialization.

| Command | Behavior |
| --- | --- |
| `start` | Ask the owned scheduled task to run in the caller's interactive session; directly launch both Hosts if that request fails |
| `launch` | Start exactly the Input and Automation roles with the controller's current token |
| `stop` | Ask the session-local Main instance to stop both roles and verify endpoint disappearance |
| `install` | Idempotently create or repair the task for the current executable |
| `uninstall` | Remove the task only when its action belongs to the current executable |

`install` alone accepts `--replace-existing` and `--authorize-portable`. They are explicit confirmations issued only after Main has shown the corresponding ownership or environment dialog. The controller never accepts an arbitrary executable path or arbitrary child-process arguments.

The stable exit codes are:

| Code | Meaning |
| --- | --- |
| 0 | requested operation completed |
| 1 | operation failed |
| 2 | operation was unavailable or timed out |
| 3 | ownership or portable-safety confirmation is required |
| 10 | service launch failed; ordinary fallback started with reduced capability |
| 11 | service launch failed; direct fallback is capability-equivalent because the controller already has an administrative token |

CLI stdout and stderr are readable English diagnostics. They are not parsed into persistent UI messages. Structured output remains unnecessary until a real external consumer exists.

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

The switch reflects actual task ownership rather than a stored Boolean. Enabling it validates the task definition, performs ownership and portable-environment confirmation, then invokes the elevated controller. Disabling it removes only the current executable's task. Cancelling a dialog or UAC restores queried state without an error toast. A successful change restarts the Host generation; duplicate operations and retries are disabled while active.

## Installed and portable trust boundary

Setup writes installation layout version 2 and registers the machine installation under HKLM. A copy counts as the current machine installation only when the registered `InstallLocation`, layout marker, and executable directory agree.

Before task registration, the elevated controller protects an installer-owned layout-2 directory. It assigns Administrators ownership, disables inherited modification rights, grants SYSTEM and Administrators full control, grants ordinary Users read/execute, and verifies the resulting owner and protected-DACL flag. Writable application data remains outside the installation directory.

Portable authorization never rewrites the directory ACL. Main instead assesses the executable environment. A writable location, non-fixed volume, reparse point, or otherwise uncertain boundary produces a warning and requires explicit confirmation, but the user may continue.

The installation directory is the intended code trust boundary. Connected peers must also resolve to the same executable path, and Host pipes restrict which local identities can reach the handshake. This is a proportional product boundary, not a claim that same-path comparison replaces all operating-system trust decisions.

## Host pipe security

Elevated Hosts create a current-user pipe DACL and apply a medium mandatory integrity label. This permits the same user's filtered ordinary Main process to connect while blocking low-integrity writers. OS-derived peer process identity is verified before the role/build/session handshake.

Task action path, working directory, task modification permissions, executable directory protection, and privileged code-loading paths are all part of the boundary. Diagnostic endpoint overrides remain a development surface and require separate review before production elevated use.

## Installer behavior

The current Inno installer:

- requests administrative installation and writes all-users registration;
- defaults to `D:\Program Files\Everywhere` when D is a fixed drive, otherwise the system Program Files directory;
- remembers only a recognized layout-2 machine installation as the next default path;
- allows an empty custom directory and warns when it is outside a Program Files directory;
- recognizes the current HKLM registration and legacy same-account HKCU registration;
- runs recognized previous Inno uninstallers before copying and requires a successful exit;
- requires the selected target to be empty after removal and rejects unrelated nonempty targets;
- installs or repairs service mode after copying, treating failure as a nonfatal degraded installation;
- invokes session-local Host stop and ownership-aware task removal during uninstall;
- preserves settings and databases stored outside the application directory.

Post-install launch uses Inno's original-user option. This works for the ordinary interactive account in the normal elevation flow, but it cannot recover the desired ordinary account when Setup itself was initially launched under different administrator credentials.

## Upgrade transaction

Every installed upgrade follows uninstall-then-install:

1. Cache recognized installation metadata before destructive work.
2. Coordinate shutdown and prevent new processes from taking installation files.
3. Run the previous uninstaller silently, wait for it, and require a successful exit code.
4. Use bounded checks for uninstaller self-deletion and directory readiness.
5. Validate the now-empty target, install the payload, protect the directory, and reconcile the task.
6. Persist the new layout metadata and optionally start Main as an ordinary user.

Replacement of required files is not an optional capability. If processes cannot be stopped or the target cannot be made ready, the upgrade fails with an actionable message. A failed replacement after the old version was removed is reported and can be retried; the installer does not promise rollback of the previous application.

## Remaining validation and work

The following items are still open and must not be described as complete:

### Task and session validation

- Smoke-test task status, registration, `RunEx`, and deletion from a trimmed published build; built-in COM interop still emits trim-compatibility warnings.
- Validate split-token administrators, true standard users, alternate installation credentials, and concurrent RDP sessions.
- Confirm the task DACL permits the filtered caller to run the task while preventing modification.
- Confirm `TASK_RUN_USE_SESSION_ID` selects the intended interactive identity for every supported account shape.

### Installation-wide shutdown

The current controller stop is scoped to the caller's user/session and exact build. Setup does not yet stop every logged-on session, hold a machine-wide replacement lock, or prove that all processes holding application files exited. A cross-version maintenance protocol or carefully bounded process-handle strategy is still required.

Alternate-credential elevation can hide the original user's HKCU legacy registration. A bootstrapper or explicit original-user discovery is required to migrate that case reliably.

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
