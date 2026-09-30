# Process isolation

These documents define the Windows and macOS Main, Input Host, Automation Host, controller, and RPC architecture. Keep each contract complete and current when responsibilities change. Linux uses its legacy in-process platform services and is outside this role model. Runtime behavior and implementation progress must be verified from code and validation evidence; they are not alternative versions of the specification.

## Document map

- [Application activation](ApplicationActivation.md) describes single-instance ownership, cross-elevation callbacks, activation acceptance, and startup delivery.
- [Runtime architecture](RuntimeArchitecture.md) describes process roles, startup, endpoint ownership, recovery, status, and platform composition.
- [RPC and Automation Host](RpcAndAutomation.md) describes handshakes, peer verification, signed resource-ID allocation, request and notification resource ownership, visual Context recovery, and typed exception transport.
- [Windows installation and Hosts control](WindowsInstallationAndHosts.md) describes Task Scheduler service mode, installation security, portable behavior, settings UX, and remaining validation.
- [Text selection monitoring](../Automation/11-TextSelectionMonitoring.md) specifies the complete native selection workflow, Main-controlled lifetime, and result ownership.

## Stable boundaries

- Main owns product state, the UI, Host generations, and authenticated RPC connections.
- Input Host owns shortcut registration, shortcut delivery, and shortcut recording, including the hooks needed by those features. It is not the exclusive owner of all native input observation.
- Automation Host owns accessibility objects, visual Contexts and targets, remote pickers, target captures, automation actions, and the complete native text-selection monitoring workflow.
- `--hosts-control` is a short-lived command surface for fixed lifecycle and installation operations. It is not a daemon and does not own Host readiness.
- Every platform entry point owns Main's single-instance lifetime and secondary-launch policy. Windows owns an early activation endpoint and a platform-local activation bootstrap helper; secondary instances forward bounded commands and exit, except autorun secondaries exit silently. macOS uses native application events with a separate direct-start instance claim; direct secondaries print a message and exit. Core consumes the optional activation service independently of platform bootstrap, and activation does not become a Host role.
- Watchdog supervises registered process handles. It is separate from the Host launch and RPC protocols.
- Native objects owned by the Automation RPC domain remain in the Host. Main receives copied result models, remote resource identities, and selected platform-neutral failures; mapped provider details remain Host-side diagnostics. Interactive screenshot snapping uses the same Host picker observations as element picking, while Main retains final pixel capture and free-form rectangle selection.

Main controls one connection-owned text-selection monitor and consumes text with its retained source. Gesture detection, debounce, accessibility reads, and clipboard fallback run together in Automation Host. See the [text-selection specification](../Automation/11-TextSelectionMonitoring.md) for control, lifetime, and verification requirements.
