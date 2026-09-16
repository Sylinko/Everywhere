# Process-isolation runtime architecture

## Process roles

On Windows and macOS, Everywhere uses the same executable for three long-lived roles and one short-lived command mode. Linux has not yet migrated to this role model.

| Role | Entry argument | Responsibility |
| --- | --- | --- |
| Main | no role argument, or `--process-role main` | UI, application state, Host lifecycle, Agent orchestration, and remote proxies |
| Input Host | `--process-role input` | global shortcut and input-hook implementation |
| Automation Host | `--process-role automation` | accessibility backend, visual Contexts, queries, picking, capture, and actions |
| Hosts controller | `--hosts-control <operation>` | fixed launch, stop, install, and uninstall operations, then immediate exit |

Role and controller dispatch occurs before Entrance, dependency injection, Avalonia, databases, or the ordinary application graph. Host processes use `ProcessRoleHostRunner` and construct only their platform session. The controller receives a small platform implementation directly from the platform entry point.

Main is intentionally ordinary privilege. On Windows, service mode elevates only the Input and Automation Hosts. Starting Main as administrator is allowed but produces a user warning because drag-and-drop interoperability may fail and Agent actions inherit broader authority.

## Startup sequence

Main registers `INamedPipePeerVerifier`, the optional Windows `IHostsServiceModeManager`, and then calls `AddProcessIsolation()` without platform callbacks. `HostProcessCoordinator` receives those services directly. macOS registers no service-mode manager, so it always selects direct launch.

For each generation Main performs this sequence:

1. Read service-mode status. Windows requests service mode only when the registered task belongs to the current executable; platforms without a manager select direct launch.
2. Invoke the current executable as `--hosts-control start` for service mode or `--hosts-control launch` for direct mode.
3. The controller starts exactly the Input and Automation roles and exits. A scheduled-task failure starts both roles directly and reports whether the fallback has equivalent or reduced capability.
4. Each Host acquires its session-scoped endpoint, constructs its role session, and waits up to 15 seconds for one Main connection and handshake.
5. Main connects, verifies the OS-reported peer process, completes the protocol handshake, and publishes the connection to consumers.

Task Scheduler acceptance, controller exit, process creation, and authenticated Host readiness are distinct states. Only the authenticated role connection makes a Host available.

## Endpoint ownership and identity

Endpoints are derived from role plus the current user/session identity. Independent interactive sessions can therefore run independent Main and Host pairs. Windows uses the first-pipe-instance flag for ownership; macOS uses an additional endpoint lease because its named-pipe implementation does not expose equivalent ownership.

A Host accepts one Main connection. That connection owns the role session lifetime, and the Host does not listen again after it ends. Competing late candidates may start, but endpoint ownership decides which process can become the live Host.

Peer verification precedes the application handshake:

- Windows obtains the peer PID from the pipe with CsWin32 `GetNamedPipeClientProcessId` or `GetNamedPipeServerProcessId`, opens it with limited query access, reads its image path, and compares the normalized full path with the current executable, case-insensitively.
- macOS obtains the peer effective user and PID from the Unix-domain socket, resolves the process image with `proc_pidpath`, canonicalizes both paths with `realpath`, and requires an exact match.

The handshake then checks role, build identity, and desktop-session identity. Claimed PID or path values are not treated as peer authentication.

## Host generation and recovery

`HostProcessCoordinator` owns one independently stoppable generation containing the two role supervisors. A generation records the requested launch route and its controller outcome. Each role then reports its own connection state:

| State | Meaning |
| --- | --- |
| `Starting` | launch, connection, or automatic reconnection is in progress |
| `Connected` | the role is authenticated with the requested or equivalent capability |
| `Degraded` | service-mode launch failed and an ordinary direct fallback connected |
| `Unavailable` | bounded startup/recovery or the controller failed without a connected fallback |

An unexpected disconnect triggers immediate reconnection/relaunch within the same generation. Three failures inside five minutes open that role's recovery circuit and publish an unavailable state. The shared Retry action stops the generation and creates a fresh one; successful service-mode installation or removal uses the same stop/restart boundary.

Main exposes independent Input and Automation status. A healthy role stays healthy when the other fails. Controller stderr is diagnostic input for logs and status classification, not a structured UI message contract.

## Shutdown

Main requests graceful role shutdown through the lifecycle RPC and waits for acknowledgement with a bounded deadline. `--hosts-control stop` connects to Main's session-scoped control endpoint rather than stealing a role endpoint, requests both roles to stop, and then checks that the role endpoints disappeared.

Host cleanup is also bounded. The role session drains first, then the RPC connection and endpoint are disposed. Failure to drain or dispose within the deadline produces a distinct Host exit failure.

The current stop protocol is session-local and exact-build. Installation-wide, cross-session shutdown remains installer work; endpoint disappearance alone is not proof that every process holding installation files has exited.

## Platform composition

The platform entry point constructs early objects because Host and controller paths intentionally do not initialize Main's DI graph.

- Windows constructs `WindowsHostsControlPlatform` and `WindowsNamedPipePeerVerifier`. The platform object implements both early controller operations and Main's service-mode management surface; Main registers the same instance in DI.
- macOS constructs `DirectHostsControlPlatform` and `MacNamedPipePeerVerifier`. Install and uninstall are no-op successes, while start requests direct launch.

`AddProcessIsolation()` registers the coordinator, connection source, Main control server, chat visual service, debugger visual Context, and their initialization hooks. Platform selection is expressed by registered services rather than delegates passed through the registration method.
