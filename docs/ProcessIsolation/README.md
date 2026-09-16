# Process isolation

These documents describe the current Windows and macOS Main, Input Host, Automation Host, controller, and RPC architecture. Linux still uses its legacy in-process platform services and is outside this implemented boundary. The implementation is the source of truth when a document and code disagree.

## Document map

- [Runtime architecture](RuntimeArchitecture.md) describes process roles, startup, endpoint ownership, recovery, status, and platform composition.
- [RPC and Automation Host](RpcAndAutomation.md) describes handshakes, peer verification, remote resources, visual Context recovery, and typed exception transport.
- [Windows installation and Hosts control](WindowsInstallationAndHosts.md) describes Task Scheduler service mode, installation security, portable behavior, settings UX, and remaining validation.

## Stable boundaries

- Main owns product state, the UI, Host generations, and authenticated RPC connections.
- Input Host owns global input hooks and shortcut delivery.
- Automation Host owns Agent-facing accessibility objects, chat visual Contexts, visual targets, remote pickers, target captures, and automation actions.
- `--hosts-control` is a short-lived command surface for fixed lifecycle and installation operations. It is not a daemon and does not own Host readiness.
- Watchdog supervises registered process handles. It is separate from the Host launch and RPC protocols.
- Native objects owned by the Automation RPC domain remain in the Host. Main receives copied result models, remote resource identities, and selected platform-neutral failures; mapped provider details remain Host-side diagnostics. Main may still use short-lived local platform objects for UI-only screenshot selection and selected-text detection, outside the Agent target domain.
