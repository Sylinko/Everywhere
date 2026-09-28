# Text Selection Monitoring

## 1. Purpose and Scope

This specification defines Windows and macOS automatic text-selection monitoring, its process boundary, and the ownership of observed text and source elements.

The feature detects a user's text selection and supplies a text attachment with an optional retained source element. Its complete native workflow belongs to the Automation Host: gesture observation, debounce, target checks, accessibility reads, clipboard fallback, and native cleanup. Main controls monitoring and decides whether to accept the resulting attachment. Input Host continues to own shortcuts and shortcut recording; it is not a broker for text-selection gestures.

Agent text paging, screenshots, and particle effects have independent contracts. Linux is outside this process-isolation specification.

## 2. Ownership and Composition

| Owner | Responsibility |
| --- | --- |
| Main | The setting, desired monitoring state, connection restoration, one result consumer, and attachment ownership |
| Automation session | At most one active text-selection monitor per authenticated connection; start/stop, source registration, direct result notification, and shutdown coordination |
| Platform monitor | Mouse observation and platform heuristics, finite detection attempts, accessibility selection reads, and clipboard fallback |
| Acquisition Context | Canonical source identity, temporary observation retentions, and accepted draft anchors |
| Input Host | Shortcut registration, delivery, and shortcut recording |

The platform entry point constructs the monitor implementation through a narrow platform contract alongside the existing Backend and picker resolver. Hosts do not initialize Main's DI graph. Reuse the session's Backend; do not create another accessibility client or Backend per detection. The Backend remains a non-retaining native service and must not own the monitor or its Context.

Main's remote monitoring service owns the connection-bound subscription and its acquisition-Context lease. Platform Backends and text-selection hooks are instantiated in the Automation Host. Native hook helpers may be reused by multiple roles in separate processes; role ownership follows the feature, not exclusive ownership of a helper class.

The monitor's lifetime is separate from delivered-resource and attachment lifetime. Disabling monitoring releases unsent observations and stops new detection. It does not reclaim anchors already enqueued for delivery or accepted by attachments. Main's connection-level receiver releases rejected results, and accepted anchors continue through the acquisition-to-chat Context move and release path.

## 3. Execution and Native Threads

```text
Main desired state -> Automation Host monitor
                       native gesture -> debounce -> target validation
                         -> selected-text read -> optional clipboard fallback
                         -> registered source + copied text
                         -> direct RPC notification -> Main result consumer
```

Windows reuses the native hook helper's dedicated message-loop thread. macOS uses a native event-tap run loop and the Automation Host's existing AppKit bootstrap. No path depends on `Dispatcher.UIThread` or an Avalonia application in the Host. Platform calls with native thread requirements use the appropriate existing native loop.

Hook callbacks only capture bounded event state and schedule detection. They do not call UIA/AX providers, wait for clipboard changes, send RPC, or wait for an asynchronous operation. Text-selection observation never consumes the user's mouse or keyboard events.

Keep at most one detection attempt executing and one latest pending trigger. Capture each attempt's target, gesture, and cursor/clipboard evidence as a stable record rather than reading mutable fields after an await. A newer relevant interaction invalidates an older unfinished attempt. Superseded unsent results release their retentions; already enqueued results keep their registered sources until Main releases them or the connection closes. Use bounded transport and avoid an unbounded per-gesture task backlog.

The long-lived listener and its debounce wait must not occupy the Context operation queue. Finite operations that acquire, read, retain, transfer, or release elements run through the owning Context's serialization boundary. A background hook must not access its identity maps or native elements directly. Stop invalidates the monitor and signals cancellation without waiting behind a provider read before doing so.

Cancellation is cooperative between native calls. A UIA/AX call that ignores its native timeout still requires the existing Host containment/recovery boundary; this spec does not introduce an abortable native-call scheduler or claim that a token can interrupt such a call.

## 4. Monitoring Control and Recovery

Use the existing authenticated Automation RPC connection and generated contracts. No Input-to-Automation connection or third Host is required.

The control surface must support these semantics; method names and operation IDs are implementation details:

| Operation | Required behavior |
| --- | --- |
| Start monitoring | Main supplies the acquisition Context and a connection-scoped monitoring identity. Repeating the same start is idempotent; another active identity cannot create a second monitor. |
| Stop monitoring | Stop the addressed identity, detach hooks, cancel unfinished work, and release unsent observations. Repeated stop succeeds; an old stop cannot disable a newer subscription or dispose a delivered anchor. |
| Result notification | Directly push the monitoring identity, observation revision, outcome, copied text, and optional anchor descriptor. Main accepts or releases the result without a follow-up fetch or claim. |

Represent monitor lifetime using the connection-scoped remote-resource mechanisms in [RPC and Automation Host](../ProcessIsolation/RpcAndAutomation.md#remote-resources). `RpcConnection` allocates positive IDs at the client endpoint and negative IDs at the server endpoint; zero is invalid. Main allocates requested Context/monitor resources, and Host allocates negative IDs for sources carried by result notifications. Both use the same registry and release machinery. Observation revisions identify ordering and are independent of these resource IDs.

Main records the desired setting separately from an active connection. Turning the setting off invalidates local acceptance immediately, even while remote cleanup is pending. The connection-level receiver stays installed and releases any anchor in a rejected notification. Before publication, Main checks the connection, monitoring identity, and latest observation revision it has received. Older asynchronous work cannot overwrite a newer published result.

An observation is a best-effort sample of a live application. Main may display one valid selection and then its successor as notifications arrive. The protocol does not promise atomic synchronization with the user's current selection or suppress every intermediate UI state.

After connection replacement, recreate the acquisition Context and monitor only if the setting is still enabled. Restore monitoring intent, not an old detection attempt. Never replay clipboard copy input or a result transfer after connection loss. Ordinary provider failures do not disable the subscription or restart a healthy Host.

During Host draining, stop accepting new triggers and detach monitoring before releasing Contexts and the Backend. Await cooperative cleanup within the existing Host shutdown deadline. Unexpected provider stalls use that existing deadline rather than a new unbounded shutdown wait.

## 5. Observation and Direct Delivery Ownership

Read selected text and retain its source during the same Context operation. `VisualElement.GetSelectedText(maxCharacters)` remains the bounded element-level operation; selected text does not become a default Snapshot field and is not implemented by Agent `read_visual_text` paging.

Validate the observed source against the intended application/window before reading. Reject Everywhere's own UI using the authenticated Main PID; `Environment.ProcessId` identifies the Host. Revalidate the target before synthetic copy input, since focus may have changed during debounce or provider calls.

If macOS finds the text on a probed child, retain that child rather than later reacquiring the focused container. If clipboard fallback produces useful text but cannot identify a reliable source element, return text without an anchor. A `Focused` locator must not be used later as a substitute for the element that supplied the text. Live observation remains best effort; retaining a source establishes its identity, not a transactionally frozen external document.

The monitor retains at most one unsent observation. Text and its source retention share one observation identity. Replacing or abandoning that observation releases its retention through the Context boundary. Delivery never reacquires focus or associates old text with a newer source.

For an observation with a source, delivery follows this sequence:

1. Host allocates a negative resource ID from its connection and registers the source as a normal acquisition-Context anchor with a cleanup registration.
2. Host enqueues one result notification containing the text, anchor ID and copied source observation, monitoring identity, revision, and outcome. A text-only result has no anchor descriptor.
3. If preparation or enqueue fails, Host releases the local registration and any untransferred retention. This local rollback does not send a remote release to Main.
4. Once enqueue succeeds, the registered anchor belongs to the delivery path. Stop, cancellation of the detector, or a newer selection cannot reclaim it. Connection teardown remains responsible if delivery cannot finish.
5. Main's connection-level receiver assumes responsibility for the anchor before applying product acceptance checks. A current result is wrapped with the correct Context lease and passed to the single consumer. A disabled, stale, rejected, or otherwise undeliverable result queues release on its originating connection, even when its Context can no longer be wrapped.

The ID sign identifies the allocating endpoint, not the resource location or release destination. Main releases a pushed negative Anchor ID through the same release queue as a positive ID from a Main-initiated request. The queue routes to the handle's originating connection and does not branch on sign. Registration, lease, cancellation, and release-before-registration guarantees are shared with other remote resources.

Main delivers an owned result to one consumer. A multicast observable cannot broadcast the same owning `RemoteVisualAnchor`. On normal acceptance, the attachment takes ownership; stale, rejected, empty, or failed delivery disposes any unconsumed anchor. The VM receives the observed text and source together and performs no focus reacquisition.

## 6. Outcomes and Failure Semantics

| Outcome | Main behavior |
| --- | --- |
| Nonempty observed text, with optional source | Apply the existing attachment replacement and capacity policy; release a rejected source |
| A valid observation establishes an empty selection | Remove the previous automatic text-selection attachment according to existing product policy |
| Superseded, cancelled, or stale observation | Preserve the attachment; discard the result and release resources |
| No supported read or inconclusive clipboard fallback | Preserve the attachment; do not report an empty selection |
| Permission denial, provider timeout, or disappeared target | Preserve the attachment and keep listening; retain diagnostic information |
| Connection loss | Invalidate old resources and restore the desired monitor on the replacement connection |

A null string from a provider or a clipboard timeout is not by itself evidence that the user cleared the selection. Carry enough outcome information to distinguish a valid empty observation from failure and inconclusive detection. Reuse `VisualElementQueryFailureKind` where it describes a provider failure; do not encode these differences using only `Text == null` or fabricate an exception for normal absence.

RPC exceptions follow the existing typed Automation mapping. Native HRESULTs and provider exception details remain in Host diagnostics. Any persistent user-facing status carries `IDynamicLocaleKey` through the contract and is resolved by a UI binding; logs remain diagnostic strings. Background detection failure does not produce a toast for each attempt or mark the whole Automation Host unhealthy for an unsupported selection provider.

## 7. Clipboard Fallback

Clipboard fallback is one finite platform operation in the Automation Host: inspect user intent, back up supported content, validate the target, send the copy gesture, wait within the existing bounded policy, read, and conditionally restore. It is never split into independent RPC calls for key-down, reading, and restoration.

Preserve Windows application exclusions, cursor heuristics, delayed-copy handling, and the `Ctrl+Insert`/`Ctrl+C` policy, including the terminal exclusions. Preserve macOS gesture heuristics, bounded child probing, and Chromium/Electron accessibility-enabling behavior. The existing selection read limit of 65,536 UTF-16 code units also bounds text returned from clipboard fallback; macOS child probing remains capped at 32.

An unchanged clipboard must not be returned as though the user had just copied the current selection. A changed sequence is evidence of a clipboard write, not proof that its content belongs to the target selection; use the available user-intent and target evidence, and skip an ambiguous fallback rather than misattribute old text.

Cleanup rules:

- Check cancellation and target identity immediately before sending input. Once input has been sent, perform necessary key-release and clipboard cleanup even if publication has been cancelled.
- Restore only while the clipboard still matches the state observed from this copy attempt. If an intervening user/application write is detected, leave it intact.
- Do not clear or rewrite the clipboard when this attempt did not establish a copy result. Preserve an originally empty clipboard when restoring a copy that the attempt can safely undo.
- Backup and restoration are best effort over the platform's supported formats: Windows selects text, DIB image, or file-drop data in that order; macOS preserves text. Additional unsupported formats do not by themselves disable copy fallback. Restoring those supported values does not promise to reconstruct every original clipboard format.
- Put cleanup in a finally path and serialize local fallback attempts, so two attempts cannot restore over one another. Do not add a global clipboard service or promise atomicity against other applications.

## 8. Acceptance Evidence

Use focused contract/ownership tests for idempotent start/stop, stale results, negative-ID anchor reception, release-before-registration, failed enqueue rollback, and cleanup of replaced unsent observations. Verify that Main- and Host-allocated IDs coexist without collision, zero is invalid, allocation cannot wrap into the other range, and negative anchors support snapshot, capture, Context move, and release. Test through production contracts rather than adding production hooks for tests. Native behavior is primarily validated through manual E2E:

- Windows and macOS: drag, double/triple click, Shift-click, normal provider selection, and platform clipboard fallback.
- Existing shortcuts keep working while selection monitoring is active, reading, failing, or disabled.
- Switch application/window during debounce, provider reads, and copy waiting; no text is attached to a newly focused unrelated element.
- Disable and re-enable during detection or while a notification is in flight; old callbacks cannot update the new subscription, received rejected anchors are released, and accepted anchors survive monitor stop.
- Replace an unsent result, reject a delivered result at attachment capacity, and disconnect during delivery; every unconsumed retention/anchor is released.
- Deliver successive selections with delayed Main processing; earlier valid results may appear temporarily, but their slower continuations cannot overwrite newer results or pair text with another observation's source.
- User copy/cut/paste during fallback, unchanged clipboard, originally empty clipboard, and supported non-text content; no stale text is reported and detected intervening writes survive cleanup.
- macOS child selection and Chromium/Electron accessibility activation; UI/native loop behavior is verified on macOS, not inferred from compilation.
- Windows elevated target in service mode and ordinary fallback Hosts; access failure leaves monitoring usable for other applications.
- Expected provider failure, unsupported selection, valid empty selection, and Host loss produce distinct product behavior.
- Shutdown and Host restart during debounce or clipboard waiting; hooks stop and cleanup respects the existing bounded Host lifecycle.

Architectural verification confirms that Windows/macOS Main obtains text-selection results through Automation RPC, that native Backend use and selection hooks belong to Automation Host, and that monitoring does not require direct Host-to-Host communication.
