# RPC and Automation Host

## Transport and contracts

Process-isolation RPC uses framed named-pipe connections with MessagePack payloads and source-generated request, response, notification, and streaming bindings. Operation IDs and payload types are fixed by the generated contracts. Each connection performs a protocol handshake before role services are considered authenticated.

The transport owns correlation, cancellation, stream completion, graceful shutdown, and connection failure. Application services own semantic retries and state recovery. An RPC request is never replayed automatically after connection loss.

## Remote resources

Host-owned objects use connection-scoped resource IDs rather than serialized native handles. Main wraps those IDs in `RpcSafeHandle` objects. A lease prevents release while one operation is using the resource; disposal queues an idempotent release on the originating connection. Closing the connection releases the Host registry and makes every resource from that incarnation invalid.

The Automation Host owns:

- one platform backend per authenticated Main connection;
- one `VisualContext` resource per chat, acquisition flow, or debugger session;
- Context-owned anchors, pickers, published targets, retentions, and native elements;
- copied capture buffers until their transport transfer completes.

Main owns `RemoteVisualContext`, `RemoteVisualAnchor`, `RemoteVisualPicker`, and copied capture wrappers. These objects preserve resource lifetime and Context identity but do not expose native platform objects.

## Automation Context execution

Each Host-side Automation Context has a single-reader operation queue. Calls that mutate one `VisualContext` therefore execute serially, while separate Contexts remain independent. Native element methods remain synchronous inside the Host because UIA and AX are synchronous provider APIs; the RPC boundary is coarse and asynchronous from Main's perspective.

`ChatVisualState` attaches lazily to the current Automation connection. When the Host connection changes, `ChatVisualService` creates a replacement remote Context and invalidates previously published target IDs. Reads and queries fail with `VisualContextResetException` until the caller re-observes. An action batch is not replayed: connection loss yields `VisualActionOutcomeUnknownException` because the Host may have performed part or all of the action before disconnection.

The debugger uses a separate diagnostics Context with no completed-turn retention. The shared acquisition Context holds draft anchors for pointer and text-selection flows before they move into a chat Context.

## Interactive picker lifetime

Beginning a picker creates a Host resource and retains its owning remote Context. Every update carries a monotonically increasing revision and a physical screen point. The Host replaces the previous native candidate and retention with the new observation.

Confirmation includes the revision that was actually presented. The Host transfers that exact retained candidate into an anchor only when the revision still matches. A successful confirmation response consumes and disposes the remote picker; a failed RPC leaves it owned by the caller so normal cleanup or a retry can release it. Closing the connection releases both resources regardless of Main-side disposal timing.

Permission denial, timeout, unsupported-provider behavior, or element disappearance while resolving an update becomes a revisioned unavailable observation with a neutral failure kind. An equivalent failure at another picker boundary may arrive as a mapped exception. The picker UX may continue after these expected failures; transport loss resets the entire remote Context.

## Exception transport

The RPC frame always carries a stable error code and a diagnostic message. A connection may additionally register one application exception mapper. The mapper serializes selected failures into an application-owned MessagePack union payload stored in the error frame; mapped failures replace the remote exception text with a fixed transport message.

Automation currently defines one union case, `VisualElementQueryRpcError`, containing `VisualElementQueryFailureKind`. The mapping is intentionally small:

| Host exception | Wire failure kind | Main exception |
| --- | --- | --- |
| `UnauthorizedAccessException` | `PermissionDenied` | `UnauthorizedAccessException` |
| `TimeoutException` | `Timeout` | `TimeoutException` |
| `NotSupportedException` | `Unsupported` | `NotSupportedException` |
| `VisualElementProviderException` | its neutral kind | the matching neutral exception |

For mapped Automation failures, native HRESULTs, platform exception types, stacks, and provider messages stay in the Host and its logs. Main receives a newly constructed local exception with the neutral meaning. Unrecognized exceptions remain `RpcRemoteException` with the generic RPC code and the current remote diagnostic message; malformed mapped payloads are protocol failures rather than silently downgraded values.

This keeps the transport generic while allowing each contract domain to add union cases only when a caller needs structured behavior. `VisualElementQueryFailureKind` is preferred when it already expresses the required semantics; a new exception contract is warranted only for a distinct recovery or control-flow decision.

## Failure ownership

Three failure layers must remain distinguishable:

1. A provider operation can fail while the Automation Host and Context remain valid. Typed Automation mapping preserves this distinction.
2. The RPC connection can close. All resources from that connection become invalid and Main replaces the remote Context on the next operation.
3. The Host process can repeatedly fail. The coordinator's recovery circuit eventually marks that role unavailable until an explicit generation restart.

Transport errors must not be converted into empty visual results, and provider errors must not be treated as proof that the Host process is dead.
