# Visual Context Platform Backend and Native Services

## 1. Responsibility

`IVisualElementBackend` is the shared platform service for root acquisition and native resources that genuinely have application or query-host lifetime. `VisualContext` is a separate platform-neutral per-chat identity, ownership, and Agent-target domain. Neither is a conversation Session, execution transaction, or worker dispatcher.

The current neutral responsibility is deliberately narrow:

- let the Backend own reusable platform clients and walkers;
- configure one stable native timeout policy before publishing those clients;
- acquire roots that have no existing receiver element into the caller's retention Context;
- choose the appropriate concrete element implementation for a root locator;
- release shared native services at application/query-host shutdown.

Existing `VisualElement` instances perform their own query, relation, action, and capture behavior directly. `VisualContext` owns their identity and logical lifetime. Snapshot Traverser owns aggregate risk and convergence.

## 2. Backend and Context Shape

```csharp
public interface IVisualElementBackend
{
    VisualElementQueryResult? Query(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? request = null);
}
```

Locator answers where acquisition begins; `Default` deliberately supplies no anchor. Resolution answers whether the result is the direct element, its nearest TopLevel, or its containing Screen; with `Default`, it instead selects the platform-default object at that same level. Resolution defaults to Direct, and an omitted scalar request means `VisualElementQueryRequest.Default`. The Backend is normally registered as a process singleton. `ChatContext` constructs its own neutral `VisualContext`; Context is not created by or registered in dependency injection. Disposing a Context releases only that conversation's elements and targets; disposing the application or future query host releases the concrete Backend and shared services. The Backend and its shared clients must not retain managed Contexts, Retentions, Elements, or callbacks that capture them.

The Backend is platform-wide, not provider-specific. Windows root acquisition may return a Win32 Screen element or a UI Automation element. macOS may eventually return NSScreen-, Application-, TopLevel-, or AX-backed elements. Concrete element types retain honest native behavior after acquisition and store the Context selected by the acquisition retention.

## 3. Direct Native Execution

UIA and AX expose object-oriented synchronous APIs over provider/RPC boundaries. The process-local model follows that reality:

- `VisualElement.Query`, navigation, and actions call the platform synchronously;
- the native platform timeout is the per-RPC safety boundary;
- the caller/Traverser decides the useful granularity of a sequence;
- no mandatory async wrapper is inserted around every native call;
- no custom worker, Channel, `TaskScheduler`, `SynchronizationContext`, Dispatcher, or watchdog participates in the current path.

This is not a claim that native providers can never hang. It is a statement about containment strength: moving a synchronous call to an in-process worker does not make that call terminable. A watchdog can observe a stuck thread and create replacement capacity, but cannot safely reclaim the thread or native call. If native timeouts prove insufficient in production, the robust boundary is the already planned whole Automation query-host process, which can report queue state, exit, and restart.

The removed worker design remains useful research history in `Refactor.md` and prior commits, but it is not a current API or migration target.

## 4. Timeout Domains

The system distinguishes several independent limits:

| Boundary | Protects | Does not protect |
|---|---|---|
| Native connection timeout | Establishing/connecting to a provider where the platform distinguishes it | A complete traversal or later provider transactions |
| Native transaction/messaging timeout | One provider operation or message | A series of individually successful calls |
| Traverser elapsed/operation budget | Aggregate Snapshot risk between native calls | A native call that ignores its own timeout and never returns |
| Text/child/result limit | Data and output growth | Provider latency by itself |
| Future query-host lifetime | Process-level crash/hang containment | Semantic correctness of a provider response |

On Windows, `IUIAutomation2.ConnectionTimeout` and `TransactionTimeout` are configured once on the shared client. Connection timeout concerns provider connection establishment; transaction timeout concerns an ordinary UIA request after routing exists. A controlled blocked-provider probe observed an existing-element `ElementFromHandleBuildCache` call following TransactionTimeout, not ConnectionTimeout. That probe does not claim to reproduce delayed first-time provider connection.

On macOS, `AXUIElementSetMessagingTimeout` is the native per-message boundary. Native app-host probes establish that configuring the retained system-wide reference applies to independently obtained Core Foundation-equal references, including references acquired before and after configuration. `MacVisualElementBackend` therefore owns one system-wide reference and configures one fixed timeout before publication; callers do not race mutable timeout policy per Context or operation.

## 5. Aggregate Risk

Platform timeout is necessary but not sufficient for a visual-tree algorithm. Snapshot Traverser must accumulate risk across calls and stop before an individually bounded sequence becomes unbounded in total.

At minimum it tracks:

- monotonic elapsed duration;
- native operation count;
- children admitted per parent;
- total nodes admitted;
- provider/PID failure evidence relevant to the current observation;
- approximate prompt/output cost;
- cancellation requested by the caller.

Checks occur before starting the next dangerous operation and immediately after one returns. A branch that times out or repeatedly fails contributes status and may be skipped best-effort. The implementation does not secretly retry, re-anchor, or move the failure to another queue.

## 6. Failure and Status Flow

Concrete platform elements convert known native failures into the neutral Automation contract. Windows converts `UIA_E_TIMEOUT` to `TimeoutException` with the original `COMException` retained as inner evidence. AX `kAXErrorCannotComplete` remains a broad provider-communication failure rather than being mislabeled as one exact timeout cause.

Snapshot records failure at the closest representable boundary:

- a known Core element keeps a skeleton node and missing-field status;
- a non-Core relation item that is already unavailable on its first structural query is omitted while its Enumerator continues, because it has neither usable fields nor a stable navigation target;
- a child edge failure attaches status to the parent;
- a root-acquisition failure attaches status to the result root/operation;
- repeated failures from one PID may produce a concise unresponsive/degraded status and suppress further risky expansion for that observation.

The Agent-facing representation uses bounded `status` text rather than a large matrix of speculative fields. Typed failures remain available internally for policy and tests.

The provider-health circuit breaker counts only `Timeout` and `ProviderFailure`. Unsupported capabilities and admitted elements disappearing from a live tree remain explicit local observations but do not consume that budget; stale non-Core relation items that never produced a structural observation are skipped as described above. A successful platform batch may also contain a failure for one requested field; that field remains missing unless the returned error establishes element unavailability or provider communication failure, and must not be promoted automatically into the provider-wide `VisualElementQueryResult.Failure` channel.

## 7. Windows Backend and Context Propagation

`WindowsVisualElementBackend` owns one process-shared `UIAutomationClient` (`CUIAutomation8`) and one Content View `UIAutomationTreeWalker`. It configures a fixed production policy of a 2-second connection timeout and a 10-second transaction timeout before the Backend becomes available. It neither creates nor stores `VisualContext` instances.

Focused, current-pointer, explicit-point, and native-window Direct queries acquire UIA elements. Current-pointer lookup reads the cursor position inside the Backend and then uses the same point-based UIA acquisition path; Core callers do not require a Win32 query facade. Windows resolves `Default + Direct` to the UI Automation desktop root, `Default + TopLevel` to the first eligible visible, non-minimized root-owner window in global Z-order, and `Default + Screen` to the primary Win32 display. Point and Pointer Screen resolution use display topology directly; Focused and NativeWindow Screen resolution first identify the top-level native window and then map it into the observed topology.

TopLevel and Focused-to-Screen resolution may require a temporary source element. Windows keeps that source in a short-lived retention owned by the query and canonicalizes only the final result into the caller retention. It does not retain every ancestor or fetch default scalar text merely to discover the containing native window.

The client is not recreated per Context, query, element, or turn. Retained UIA element references may be operated through the shared client regardless of the client that originally returned their native pointer. The application serializes mutation of each individual Context, while UIA itself may service calls from different Contexts concurrently.

macOS resolves `Default + Direct` to an isolated AX system-wide object and `Default + TopLevel` to the first eligible window in global Z-order. `Default + Screen` resolves the primary NSScreen. Point and Pointer Screen resolution use display topology directly; Focused and NativeWindow Screen queries resolve the containing AX window and assign it to the display with the largest visible intersection. A fully off-screen window has no Screen relation.

Every root method receives a `VisualElementRetention`; `retention.Context` selects the destination identity domain. On a UIA or Screen identity-map miss, the Backend creates a concrete element with immutable references to that Context and this Backend. On a hit, it returns the Context-local canonical element. This means equal UIA RuntimeIds in two Contexts create two high-level elements, while repeated acquisition inside one retained Context reuses one high-level element.

Windows also has native facilities that are not UIA:

- `WindowsDisplayTopology` observes displays through Win32;
- `GDIScreenCapture` captures Screen pixels;
- native input helpers handle foreground, pointing, and `SendInput`;
- `ScreenVisualElement` owns logical monitor identity but no releasable provider object.

The Backend selects the root implementation. After that, each concrete element owns its behavior and may return another implementation through a relation. Relation code propagates the origin's Context and Backend; it does not ask the caller to resupply either one.

## 8. Windows UI Automation Interop

The replacement backend consumes UI Automation through `Everywhere.Windows.Interop`, generated by CsWin32 with runtime COM marshaling disabled. Generated ABI declarations remain internal; the public facade exposes deterministic native ownership and bounded high-level UIA operations without exposing raw generated pointer types to the Automation layer.

### 8.1 Ownership Types

`ComReference` stores one native pointer, provides disposed access, and releases exactly one reference. Deterministic `Dispose` is the normal path. Its finalizer is a last-resort leak fallback and suppresses finalization after successful disposal.

Native calls that discover, navigate to, or refresh an element return a stack-friendly `UIAutomationElement` struct. It owns exactly one returned COM reference and is disposed with `using`. It can expose values from the cache attached to that result without allocating a durable CLR wrapper.

`UIAutomationElement.Realize()` has explicit AddRef semantics: it creates an independent long-lived `UIAutomationElementReference` while leaving the operation-local owner valid. Disposing the local result releases its original reference; the durable Reference later releases the AddRef. A durable Reference may itself acquire another temporary AddRef when an operation needs an independently owned local element.

The high-level `UIAutomationVisualElement` retains one durable Reference. Application and Agent callers do not obtain that Reference directly; they use the concrete element's neutral operations.

### 8.2 Cache Requests

Requested scalar properties and patterns are collected into one operation-local `UIAutomationCacheRequest` when possible. Cache requests are never retained across unrelated calls. UIA does not implicitly cache a Pattern's properties when that Pattern is added: each `Cached*` read must have a corresponding property in the request. Pattern-only operations such as `SetText` must not fetch the Value property merely to acquire ValuePattern, because that could transfer an unbounded provider value before mutation.

Required behavior includes:

- use `TreeScope.Element` for bounded navigation results;
- navigate immediate parent, first child, or adjacent sibling with the matching TreeWalker `*BuildCache` call;
- continue siblings lazily rather than requesting unbounded `Children` or `Descendants` subtrees;
- include RuntimeId for unknown-element acquisition/navigation;
- omit RuntimeId when refreshing an already canonical element;
- copy scalar values before releasing BSTR, SAFEARRAY, VARIANT, Pattern, CacheRequest, and operation-local element owners;
- treat unsupported properties/patterns differently from unavailable elements and provider failures.

Common scalar text uses cached Value when that is what the provider exposes. Document-capable providers may use TextPattern and bounded `DocumentRange.GetText(maxLength)`. A complete Value payload may still cross the native boundary before local truncation; TransactionTimeout and Traverser risk are the safety boundaries.

### 8.3 RuntimeId Canonicalization

UIA may return different COM pointers for the same RuntimeId. The Context-owned identity map therefore operates before a new high-level element is exposed.

For an unknown operation-local element:

1. read its cached RuntimeId only inside a callback while the SAFEARRAY view is valid;
2. probe `VisualElementIdentityMap<UIAutomationRuntimeId>` through `IAlternateEqualityComparer<ReadOnlySpan<int>, UIAutomationRuntimeId>`;
3. on a hit, retain and return the existing `UIAutomationVisualElement`, then release the temporary native result;
4. on a miss, copy the RuntimeId, Realize one durable Reference, create the canonical element, and retain it before return.

This avoids durable-wrapper and RuntimeId allocation on the common duplicate path. The map is independent of COM pointer identity and does not retain entries by itself. When the final real ownership batch leaves, the high-level element releases its Reference and the active RuntimeId incarnation ends.

The application guarantee is: while the last element for a RuntimeId remains retained, newly observed equal RuntimeIds resolve to the same managed reference. It does not claim that RuntimeId can never be reused after the final reference is gone.

### 8.4 Empirical Windows Evidence

Retained probes on Windows 11 build 26100 with .NET 10.0.11 observed:

- an element obtained through client A could execute `BuildUpdatedCache` on another MTA worker with client B's CacheRequest;
- transaction timeout followed the client/cache request used for the current call rather than the worker/client that originally obtained the element;
- one shared `CUIAutomation8` allowed a responsive-provider call on another MTA worker to progress while a different provider call through the same client was blocked;
- repeated acquisition and Parent navigation returned distinct controlling `IUnknown` values while retaining one RuntimeId;
- sharing a client did not eliminate native returned-element allocations;
- concurrent `CUIAutomation8`/ContentViewWalker initialization sometimes returned `E_FAIL`, so Windows serializes only that one-time initialization segment.

These probes support the current shared immutable-policy client and RuntimeId identity map. They are characterization evidence, not a documented guarantee of every Windows/UIAutomationCore version. The probe code remains intentionally available for future baselines.

## 9. Screen and UIA Composition

Windows display topology is intentionally simple:

- first use enumerates displays and publishes an immutable observation;
- the shared hidden `MessageWindow` receives `WM_DISPLAYCHANGE`;
- each notification re-enumerates displays, increments a generation, and atomically replaces the observation;
- no polling, structural diff, or independent topology service is used;
- callers read the current immutable observation directly;
- displays are ordered top-to-bottom, then left-to-right, with stable tie-breakers;
- Screen identity combines topology generation and `HMONITOR`;
- an element or Enumerator bound to an older generation reports unavailable rather than silently retargeting.

The composed relations are:

- a top-level UIA window resolves its monitor with `MonitorFromWindow` and returns that Screen as Parent;
- a Screen lazily enumerates eligible visible, non-minimized top-level windows on that monitor;
- top-level sibling order follows Screen child order;
- descendant UIA navigation remains Content View provider order.

This is best-effort topology. `WM_DISPLAYCHANGE` is the accepted change boundary, not proof that every relation remains stable between calls. A Screen's borrowed `HMONITOR` has no physical release operation, but its logical incarnation still follows Context retention.

Capture returns bitmap pixels without a DPI property. IVisualElementCapture.Bounds is the represented region in the platform desktop coordinates consumed by Avalonia: top-left desktop points on macOS, desktop pixels on Windows/X11. Size is the output pixel resolution, with each dimension at most 4096. They need not have equal dimensions. The Avalonia adapter attaches normalized 96-DPI presentation metadata when it constructs a Bitmap; positioning uses capture.Bounds, not a separately queried element rectangle or a Retina multiplier applied to global positions.

Determine capturable coverage before applying output-resolution limits. DWM clips against its reported source surface rather than the desktop or UIA TopLevel rectangle, then scales the translated visual into a bounded composition target and frame pool before CPU readback. GDI intersects the requested rectangle with the virtual screen and performs a 1:1 BitBlt before a bounded copy, so its intermediate DIB can still be larger than the output. A final resize is skipped when the result is already within the limit. Scaling never changes represented Bounds or upscales a small image. A 4096-square BGRA output is still 64 MiB, excluding intermediate storage.

The Bounds origin follows the source-to-screen mapping used by the capture operation. Window movement and native frame production remain best effort, not an atomic observation. Source elements need only remain retained until capture completes; independent copied image buffers can outlive those elements. Existing RPC transport is unchanged.

macOS draws the selected CGImage into a bounded CGBitmapContext before allocating a full-size copied pixel buffer. AX capture retains BestResolution and the FullSize Stage Manager workaround, deriving image density from actual image dimensions and the observed window coverage rather than an arbitrary screen's backing scale. A four-quadrant native AppKit probe verifies RGBA byte order, top-left row orientation, descendant cropping, and exact FullSize-to-AX-bounds mapping for ordinary shadowed, borderless, minimized, fully occluded, and partially off-desktop windows on macOS 15. The same contract passes in an active Stage while Stage Manager is globally enabled; inactive Stages and separate Spaces remain uncharacterized. Occlusion does not replace the captured window surface with the front window, and a half-off-desktop window returns its complete surface through both a retained AX element and fresh NativeWindow acquisition. A window with zero intersection against every display remains directly reacquirable by Quartz ID but has no Screen relation, and `CGSHWCaptureWindowList` returns no image; fully off-desktop capture is therefore an explicit macOS limitation and the Backend does not reposition another application's window as a fallback. Screen capture uses the explicit four-parameter window-list overload with `OnScreenOnly`; the two-parameter relative-window overload returns null for window number zero even when TCC preflight succeeds. NominalResolution is a future animation-only quality choice, not a desktop-coordinate conversion. Linux XGetImage requires a viewable window and a rectangle within its screen; its temporary image is copied/resampled to bounded owned storage and then destroyed once. Neither backend is claimed natively verified by Windows builds.

Windows normally projects cached UIA BoundingRectangle coordinates directly. For a UIA element backed by a top-level HWND, a successful `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` replaces that rectangle with the visible compositor frame; failure falls back to the cached UIA rectangle. This preserves the established capture-alignment behavior while leaving descendant bounds provider-defined. Capture of a minimized top-level window remains a known limitation even when child-element capture works.

## 10. Standard Default Invocation

Platform-neutral `VisualElement.Invoke` means “perform this element's semantic default action.” Windows creates one operation-local cache containing Invoke, Toggle, SelectionItem, ExpandCollapse, and LegacyIAccessible patterns. It tries the standard Patterns in that order, followed by the MSAA-compatible LegacyIAccessible default action.

Pattern absence is the only reason to advance. Once an available pattern is called, any HRESULT failure is propagated because the provider may already have mutated state. ExpandCollapse expands Collapsed and PartiallyExpanded elements, collapses Expanded elements, and treats LeafNode as unsupported by that mechanism. Every Pattern reference is released on every path.

Only total absence of both a usable standard Pattern and LegacyIAccessible advances to physical clickable-point fallback. A successful Legacy `DoDefaultAction` terminates the operation; its HRESULT failure also terminates because the underlying MSAA action may already have occurred. The fallback obtains and validates `GetClickablePoint`, resolves the root-owner window, verifies the point still belongs to that window, normalizes against the virtual desktop, and submits one left-click input batch. It handles negative coordinates and non-primary displays. An attempted UIA or MSAA action is never followed by an automatic click retry.

This remains an experimental default-action heuristic. UIA exposes many other patterns, and several supported patterns may represent independent behaviors. RangeValue, Scroll, Transform, Window, Dock, MultipleView, text ranges, and other argument-bearing operations require explicit Agent operations rather than hidden fallback precedence.

Provider success proves only that the provider accepted a call. It cannot universally prove the application reached the Agent's intended semantic state. Later observation may provide evidence, but the platform layer does not issue duplicate actions to manufacture certainty.

## 11. Programmatic Text Replacement

`VisualElement.SetText` replaces complete editable scalar text. Windows requests ValuePattern, IsEnabled, and ValuePattern.IsReadOnly in one operation-local cache. It rejects disabled or read-only elements before calling `SetValue`; the provider remains authoritative if state changes after the observation.

Pattern absence is unsupported. Provider HRESULTs and timeouts use the normal failure boundary. The implementation allocates a length-preserving BSTR, calls the cached Pattern, and releases both on all paths. Empty text clears the value and embedded nulls retain their BSTR length.

This path does not focus the element and does not use TextPattern, LegacyIAccessible, clipboard, or simulated keyboard input. A later explicit input strategy may be useful for broken providers, but it must not type invisibly after successful `SetValue`.

## 12. Selected Text

`VisualElement.GetSelectedText(maxCharacters)` is a separate bounded observation rather than part of ordinary scalar Snapshot queries. Windows refreshes Text, Selection, and LegacyIAccessible Patterns together. It first concatenates nonempty TextPattern selection ranges in provider order. When no text range contains content, it reads selected child elements through SelectionPattern and finally through LegacyIAccessible's normalized MSAA selection. Selected child labels prefer the UIA Name, then Legacy Name and Value, and multiple labels are newline-separated.

All paths share one UTF-16 character budget and a fixed 256-part operation ceiling so provider-controlled range or child collections cannot create unbounded RPC traffic. A missing Pattern, missing selection collection, or only empty ranges/items advances to the next compatible mechanism and eventually returns null. Every range, selected element, collection, Pattern, CacheRequest, and operation-local element is released on every path. Provider HRESULTs and timeouts use the normal failure boundary rather than being treated as an empty selection. Clipboard simulation remains an orchestration fallback in the user-selection detector rather than hidden element behavior.

## 13. Focus and Shortcut Delivery

`VisualElement.Focus` acquires a temporary native element reference and calls UIA `SetFocus`. It does not substitute a containing HWND through `AttachThreadInput`; the retained UIA element is the intended object-oriented target. Provider success does not separately guarantee foreground activation.

`SendKeyGesture` accepts Avalonia's immutable `KeyGesture`, including unmodified keys. Windows refreshes the element's native handle when needed, searches a bounded Content View parent chain for the nearest nonzero handle, resolves the Win32 root-owner, and requires foreground activation before injection. If Windows foreground policy refuses activation, no input is sent.

The centralized injector:

- releases physically held modifiers not requested by the gesture;
- preserves requested modifiers already down;
- presses only missing requested modifiers;
- emits the main key down/up pair;
- releases only modifiers it pressed;
- stamps every event with one stable public Everywhere magic;
- requires `SendInput` to accept the complete batch.

Deliberately released user modifiers are not restored because restoring after a concurrent physical release can create a stuck logical key. UIPI may still reject input into a higher-integrity target. Successful insertion proves only that Windows accepted the events.

## 14. Visibility and Input Guard

Bring-into-view is separate from invocation. A virtualized placeholder may need VirtualizedItem.Realize; an off-screen item may support ScrollItem.ScrollIntoView. Either call can change the viewport, identity incarnation, supported patterns, or bounds, so action policy must re-observe before continuing. Neither call is a universal prerequisite or guarantee.

The planned user-visible guard is orchestration policy, not Backend or element lifetime:

1. show a mouse-through, non-activating overlay explaining that Everywhere is reading the screen;
2. exclude it from observation with `UIA_WindowVisibilityOverridden` and defensive HWND filtering;
3. use `WH_KEYBOARD_LL` and `WH_MOUSE_LL` on a message-loop thread;
4. block physical/unrelated injected input while active;
5. allow only Everywhere input carrying the fixed magic and injected flag;
6. treat physical Escape as cancellation;
7. handle keys/buttons already down at entry;
8. remove hooks and overlay on every exit path.

Hook callbacks perform constant-time checks only. The fixed magic is a classifier, not a credential or per-operation random token. UIA pattern calls need no low-level input tag.

The overlay may reduce user-driven mutation but does not create a native tree transaction, immutable Snapshot, or element owner. It can be introduced independently of the deleted execution Scope design.

## 14. macOS Backend

The macOS Backend is a platform service rather than an AX-only service. `MacVisualElementBackend` preserves distinct AX, AX system-wide, and NSScreen domains through Context-owned concrete elements and owns the process-shared AX system-wide reference and display topology.

### 14.1 Accessibility Backend

Implemented AX behavior includes:

- use `AXUIElementSetMessagingTimeout` as the native per-message safety boundary;
- avoid racing mutable process-global timeout policy;
- use `AXUIElementCopyMultipleAttributeValues` to batch scalar attributes while preserving per-position errors;
- keep generic or malformed per-position results field-local after the enclosing batch succeeds; `InvalidUIElement`, `CannotComplete`, and `APIDisabled` still retain their element/provider-wide semantics;
- use `AXUIElementGetAttributeValueCount` plus `AXUIElementCopyAttributeValues(index, maxValues)` for bounded large arrays;
- prefer `AXNumberOfCharacters`, parameterized `AXStringForRange`, and `CFRange` for ranged document content where supported;
- treat a successful but empty `AXStringForRange` for a positive character count as non-progressing ranged access and fall back to bounded local paging over `AXValue`; a missing fallback is unsupported rather than a provider failure;
- treat generic `kAXErrorFailure` from the optional `AXValue` text fallback as unsupported only in that fallback context, because observed providers may advertise `AXValue` on non-text elements yet fail to produce it; the original `AXException` remains diagnostic evidence, and whole-transaction failures are not reclassified;
- expose `NativeWindowHandle` only on a real `AXWindow`; descendants do not inherit the enclosing Quartz identifier, while capture may resolve `AXWindow` internally as an operation detail;
- accept that ordinary string attributes have no maximum-length argument and may require one timeout-bounded complete read followed by local truncation;
- implement sibling navigation through the macOS 26 parameterized child-index attribute when the current provider supports it, a validated Context-incarnation metadata hint otherwise, and bounded parent/child scanning as the final fallback;
- preserve Create/Copy ownership and distinguish unsupported, permission, destroyed-element, and provider communication failures.

Native app-host probes establish the Core Foundation types, equality of independently obtained `AXUIElementRef` values, fixed messaging-timeout scope, and the implemented provider behaviors. Cross-compilation remains only a compile check; unresolved native cases stay explicit in repository-root `temp.md`.

### 14.2 Topology Design Checkpoint

The topology checkpoint is accepted as follows:

- Screen -> AXWindow -> AX descendants is the canonical structural Parent/Child chain;
- AXApplication is excluded from that main chain and may later appear through an Application/Owner relation or projection grouping;
- the AX system-wide element is an isolated special root for direct queries and is not traversable;
- a Screen lazily enumerates eligible top-level AX windows assigned to that display in global Z-order;
- top-level sibling order follows the containing Screen's child order;
- descendant sibling order follows the parent's bounded AXChildren order;
- display topology is rebuilt and atomically replaced after `NSApplicationDidChangeScreenParametersNotification`.

Native probes have established descendant/window/Application relationships, ordering of multiple windows, focused/minimized/hidden/closed windows, panels, sheets, destruction, equality across acquisition paths, and capture coordinates on the current unrotated Retina display. The same full-size window and descendant crop checks pass in an active Stage while Stage Manager is globally enabled; inactive Stages and separate Spaces remain uncharacterized. Remaining validation includes rotated and mixed-density displays, multi-display and spanning-window assignment, permission transitions, key/full-screen/windowless cases, and repeated page-boundary mutation.

Screen grouping is structural and does not duplicate live AX identity: the same AX window canonicalizes within its destination Context regardless of acquisition path. Screen elements and relation Enumerators reject use after their topology generation is replaced. Window-to-display assignment uses largest visible intersection and does not derive Application ownership from focus or pointer location.

## 15. Other Platforms and Fallbacks

Every platform follows the same high-level boundaries:

- Backend owns root acquisition and shared platform services without retaining caller Contexts;
- neutral Context owns its per-chat identity, lifetime, and Agent-target domain;
- concrete elements retain honest native identity and behavior;
- Context owns canonical identity and real retention batches;
- unsupported mechanisms degrade explicitly;
- Snapshot and Agent-facing code do not depend on backend count.

If a provider cannot batch requested fields, it may perform individual reads. Every fallback remains within the requested field set, per-call native timeout, Traverser aggregate budget, text/collection limits, and provider-failure policy. A fallback must never turn one bounded request into an unbounded sequence.
