# RPC and Automation Host

## Transport and contracts

Process-isolation RPC uses framed named-pipe connections with MessagePack payloads and source-generated request, response, notification, and streaming bindings. Operation IDs and payload types are fixed by the generated contracts. Each connection performs a protocol handshake before role services are considered authenticated.

The transport owns correlation, cancellation, stream completion, graceful shutdown, and connection failure. Application services own semantic retries and state recovery. An RPC request is never replayed automatically after connection loss.

## Remote resources

Remote resources are addressed by a nonzero signed `long` scoped to one authenticated connection. They represent ownership of a Context, Anchor, Picker, or another releasable object, not a native handle or native element identity. Two anchors may retain the same canonical element under different resource IDs.

### ID allocation

`RpcConnection` owns one thread-safe resource-ID allocator per endpoint. All resource kinds allocated by that endpoint share it:

| Allocating endpoint | Sequence |
| --- | --- |
| Client (`IsServer == false`) | `1, 2, 3, ...` |
| Server (`IsServer == true`) | `-1, -2, -3, ...` |
| Neither endpoint | `0` is invalid |

For a Main-to-Host connection, Main allocates positive IDs and Host allocates negative IDs. The allocator never reuses an ID within its connection and fails on exhaustion without wrapping into zero or the other endpoint's range. Independent connections may use the same numeric values; a remote handle always retains its originating connection.

The sign identifies the allocator, not the location of the resource or the owner of its remote handle. A Main-requested anchor with ID `17` and a Host-pushed anchor with ID `-4` both reside in Automation Host and are both released by Main through the same connection. Release routing never depends on the sign.

Resource IDs remain distinct from RPC correlation IDs, positive Agent target IDs, native identities, and observation revisions. Only resource-ID validation accepts either sign. A capture request carrying an anchor uses the nonzero resource-ID rule; one carrying an Agent target uses the positive target-ID rule.

### Registration, handles, and release

The endpoint containing the actual resource registers its cleanup owner in `RpcRemoteResourceRegistry`. The endpoint using it remotely wraps its ID in `RpcSafeHandle`. Both signs use the same registry, handle, lease, and release machinery.

`AcquireLease()` keeps a handle alive while an operation uses it. An anchor also holds a lease on its parent Context. Disposing the handle queues an idempotent release through `RpcSafeHandleReleaseQueue` on the originating connection. The queue only delivers remote releases; it does not allocate IDs or dispose locally registered objects. A local rollback uses the local registry and its cleanup path.

Request-driven creation allocates the ID before dispatch. The requester therefore knows what to release even if it cancels before receiving the response. If release arrives before registration, the registry records that ID and disposes the resource when registration occurs. `Consume` removes a cleanup registration only after another operation has already transferred or consumed its ownership. IDs are not recycled after release or consumption.

Closing a connection releases its local resource registry and invalidates the peer's handles for that incarnation. An old release cannot be redirected to a replacement connection even when the numeric ID is reused there.

### Resources carried by notifications

An endpoint may register a local resource using its own ID range and push its descriptor in a notification. For an Automation anchor, Host allocates a negative ID, registers the retained source, and sends the ID and copied observation to Main. Main takes ownership through the normal remote-anchor wrapper and release queue. There is no separate result-claim or application-acknowledgement operation.

| Delivery boundary | Resource responsibility |
| --- | --- |
| Before notification enqueue | The sender owns cleanup; registration, serialization, or enqueue failure rolls back the local resource. |
| After successful enqueue | The registration remains available to the receiver. Successful enqueue means transport acceptance, not attachment acceptance. The sender must not reclaim this resource because monitoring stopped or a newer observation arrived. |
| Receiver accepts the result | The normal remote handle transfers to its application owner. |
| Receiver rejects or cannot present the result | The connection-level receiver queues release for its resource ID, including when the feature subscription is already disabled. |
| Connection closes before delivery or release | Session teardown releases the remaining registration. |

The notification receiver remains installed for the connection lifetime, independently of a feature's current subscription. It assumes cleanup responsibility before checking enablement or observation freshness. When a usable parent Context is no longer available, it releases the received ID directly rather than constructing a handle against an invalid Context. Receiver error paths release any descriptor whose ownership has not transferred. This reuses the ordinary release mechanism, with no second registry or result-expiry protocol.

### Automation ownership

The Automation Host owns:

- one platform backend per authenticated Main connection;
- one `VisualContext` resource per chat, acquisition flow, or debugger session;
- Context-owned anchors, pickers, published targets, retentions, and native elements;
- copied capture buffers until their transport transfer completes;
- one connection-owned text-selection monitor and its unsent native observation.

Main owns `RemoteVisualContext`, `RemoteVisualAnchor`, `RemoteVisualPicker`, and copied capture wrappers. These objects preserve resource lifetime and Context identity but do not expose native platform objects.

## Automation Context execution

Each Host-side Automation Context has a single-reader operation queue. Calls that mutate one `VisualContext` therefore execute serially, while separate Contexts remain independent. Native element methods remain synchronous inside the Host because UIA and AX are synchronous provider APIs; the RPC boundary is coarse and asynchronous from Main's perspective.

Caller cancellation is transport cancellation: Main cancels its pending correlation and the Host receives the request token. An operation-local deadline is application policy instead. Snapshot traversal links its own deadline token to the request token so platform cursors can observe both between native calls, but it consumes only its own deadline and returns a partial result with status. It never cancels the request token or turns an aggregate Snapshot deadline into an RPC cancellation frame. A synchronous native provider call that does not return cannot observe either token; the Host process remains the reclaimable boundary.

`ChatVisualState` attaches lazily to the current Automation connection. When the Host connection changes, `ChatVisualService` creates a replacement remote Context and invalidates previously published target IDs. Reads and queries fail with `VisualContextResetException` until the caller re-observes. An action batch is not replayed: connection loss yields `VisualActionOutcomeUnknownException` because the Host may have performed part or all of the action before disconnection.

The debugger uses a separate diagnostics Context with no completed-turn retention. The shared acquisition Context holds draft anchors for pointer and text-selection flows before they move into a chat Context.

## Interactive picker lifetime

Beginning a picker creates a Host resource and retains its owning remote Context. Every update carries a monotonically increasing revision and a physical screen point. The Host replaces the previous native candidate and retention with the new observation.

Confirmation includes the revision that was actually presented. The Host transfers that exact retained candidate into an anchor only when the revision still matches. A successful confirmation response consumes and disposes the remote picker; a failed RPC leaves it owned by the caller so normal cleanup or a retry can release it. Closing the connection releases both resources regardless of Main-side disposal timing.

Permission denial, timeout, unsupported-provider behavior, or element disappearance while resolving an update becomes a revisioned unavailable observation with a neutral failure kind. An equivalent failure at another picker boundary may arrive as a mapped exception. The picker UX may continue after these expected failures; transport loss resets the entire remote Context.

Element selection and screenshot snapping share this observation path. Element confirmation transfers the retained candidate into a remote anchor. Screenshot confirmation uses only the copied observation bounds, releases the picker, and captures pixels in Main; free-form screenshot selection remains entirely Main-local. Mode changes invalidate older in-flight observations so a late snapped result cannot overwrite a newer mode or free-form rectangle.

## Text-selection monitoring

The complete monitoring workflow runs inside Automation Host, with Main-controlled start/stop over the authenticated connection. Host directly pushes the outcome, copied text, optional registered anchor descriptor, monitoring identity, and observation revision. Main does not fetch or confirm the observation in another RPC. The full feature contract is in [11-TextSelectionMonitoring](../Automation/11-TextSelectionMonitoring.md).

Monitoring is a long-lived session resource, not a long-running Context queue operation. Hooks and debounce are independent; finite native element reads and retention transfers use Context serialization. Main restores only desired monitoring state after reconnection, never a clipboard copy attempt.

The monitor retains at most one unsent observation and releases it on replacement or stop. Once enqueued, its anchor follows the notification ownership rules above. Main releases results rejected by subscription identity or observation ordering; accepted anchors outlive monitor stop under their attachment owners. Text and source always refer to the same Host observation without a later focus query. Sequential display of two valid observations is allowed; an older asynchronous completion must not overwrite a newer published result.

Expected background provider failures leave the monitor active. Native diagnostics remain Host-side, and persistent UI messages retain dynamic localization structure.

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
