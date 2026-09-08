# Visual Context Refactoring Specification

> This original monolithic specification is retained as design history. The numbered documents beginning with [01-Overview](01-Overview.md) are the current source of truth and may intentionally supersede API shapes shown below.

> **Current architecture correction:** the design history below predates the final execution and lifetime review. The implemented platform-neutral Element API is synchronous (`Query`, `CreateEnumerator`, `Invoke`, `SetText`, `Focus`, and `SendKeyGesture`) and calls the concrete platform directly. One shared `IVisualElementBackend` owns reusable platform services and one Locator + Resolution root query, while each `ChatContext` directly owns a sealed platform-neutral `VisualContext` for identity, retention, and Agent targets. A root query selects its Context only through `VisualElementRetention`; that Context is propagated through the resulting concrete elements and relation results. Native timeout is the per-RPC boundary, Traverser owns aggregate risk, and future query-host process isolation is the stronger containment boundary. Scope, Direct Scope, worker dispatch, custom TaskScheduler/SynchronizationContext, watchdog, operation pins, per-Scope clients, platform Context subclasses, and Context factories were implemented experimentally and then removed. Element lifetime is expressed only by real-owner `VisualElementRetention` batches and current/historical Agent turns. Snapshot now lives in `Everywhere.Automation`; planning and PromptNode construction are merged into one Core `VisualContextPromptBuilder` rather than the historical separate Planner and builder contracts. The current projection uses `PromptCompactElement` for compact XML-like markup with quoted attributes and sparse bare flags; it does not publish speculative action capabilities or normal-state fields. The Agent-facing structural query is also being simplified so Element and Composite targets share one query operation rather than requiring an Inspect-versus-Expand choice. See [Architecture](02-Architecture.md), [Element Model](03-ElementModel.md), [Platform Backend](04-PlatformRuntime.md), [Snapshot Pipeline](05-SnapshotPipeline.md), and [VisualQuery](06-VisualQuery.md) rather than copying the historical Store/Scope/session/worker/pipeline examples in this file.

## 1. Decision Summary

The current `VisualContextBuilder` mixes platform-provider traversal, visibility propagation, structural repair, text merging, budget allocation, ID assignment, serialization, and agent-tool semantics. It also assumes that every rendered integer ID resolves to one legacy `Everywhere.Interop.IVisualElement`, which is incompatible with compressed representations of complex visual subtrees.

This work is a replacement of the internal model and query contract, not a sequence of patches to the existing builder. The target architecture keeps three implementation phases and adds an explicit agent interaction boundary:

```text
ChatContext
    |
VisualElementStore <-----------------------------+
    ^                                             |
    |                                             |
VisualContextScope                                |
    |                                             |
    `-> Snapshot -> Plan -> Build PromptNode -----+
            ^                    |
            |                    `-> publish Element / Composite targets
            |
   platform accessibility boundary
```

1. **Snapshot** is the only phase allowed to access platform accessibility APIs. It preserves the existing Weighted BFS while returning a bounded, partial observation of a potentially enormous live visual tree. The name describes the returned facts, not an immutable platform tree or provider lock.
2. **Plan** is a pure in-memory phase. It normalizes the observed forest, coalesces repeated roots, separates descendant relevance from self-visibility, creates queryable `Composite` projections, and allocates an approximate output budget.
3. **Build PromptNode** owns model-facing structure, escaping boundaries, cost estimation, and late-bound Store IDs. It publishes only the targets represented by the completed prompt through an atomic `VisualElementStore` batch. Its content result is a `PromptNode`, not an eagerly flattened string.
4. **VisualContextScope** is the stateful execution and safety boundary for one visual operation. It owns bounded platform access, transaction state, enumeration, observation caches, deadlines, and the temporary input guard, but it does not own retained visual elements. `VisualContextService` only enters this scope; it does not expose a separate `ReadAsync` pipeline.
5. **VisualQuery** replaces `get_visual_tree` as the agent-facing retrieval contract. It queries either one real element or one logical `Composite` target using bounded operations.
6. **Everywhere.VisualContext** is the domain namespace and assembly boundary. It owns platform-neutral contracts and the replacement pipeline. The reusable prompt tree lives in a lower-level `Everywhere.Prompting` assembly so Visual Context can produce `PromptNode` directly without referencing Core.
7. **VisualElementStore** is associated with a `ChatContext` and is the maximum lifetime boundary for every `VisualElement` and Agent-visible target it contains. It preserves target identity across multiple operation scopes and later applies LRU or other evidence-based retention heuristics.

The architecture intentionally avoids a generic middleware framework, an exact tokenizer abstraction, a global UI snapshot, and semantic heuristics that attempt to repair every mutation of a live tree.

## 2. Goals, Premises, and Non-Goals

### 2.1 Goals

1. **Bound all platform work.** A stalled provider, a huge text element, or a container with hundreds of thousands of children must produce a partial result instead of an unbounded operation.
2. **Preserve the tuned relevance algorithm.** Weighted BFS, `TraverseDistance`, direction weights, type weights, core priority, and visited-element deduplication remain the canonical ordering system.
3. **Compress structure without losing queryability.** Many fragmented text nodes or a high-overhead mixed subtree may become one `Composite`, while retained source members and interactive descendants remain inspectable.
4. **Make target identity honest.** A real element ID resolves to exactly one `VisualElement`. A `Composite` ID resolves to a `CompositeTarget`; it must never alias the first source element.
5. **Support bounded follow-up retrieval.** Large composites, child collections, and text content expose 1-based `offset`, clamped `limit`, `nextOffset`, and `hasMore` semantics similar to `read_file`.
6. **Keep status explicit.** An omission, timeout, incomplete enumeration, unavailable field, degraded guard, or unresponsive provider becomes bounded status attached to the affected node or root rather than a fabricated platform element or a silent empty value.
7. **Remain deterministic for equivalent observations.** Equivalent snapshot inputs, settings, and initial Store state must produce the same ordering, composites, status messages, and final IDs.
8. **Remove legacy entanglement.** The final implementation must delete the old DTO, pre-render ID assignment, format selection by detail level, and `get_visual_tree` contract after callers are migrated.

### 2.2 Accepted Premises

- **The visual tree can be arbitrarily large and frequently mutable.** No phase may assume that an entire application, web page, virtualized list, or accessibility subtree can be materialized safely.
- **A read is best effort.** The target application can update itself while the user is blocked. Equivalent follow-up queries are not guaranteed to observe identical live state.
- **VisualContextScope is not a snapshot or lock.** It reduces user-driven changes and owns bounded observation state, but timers, animations, network updates, and provider reconstruction can still mutate the tree.
- **Continuation is simple and agent-directed.** The platform performs a bounded read from the requested offset. It does not use expensive fingerprints, similarity matching, or automatic re-anchoring to pretend the tree is stable.
- **Token counts are approximate.** Model providers use different tokenizers. The selected prompt projection and `PromptNode` renderer provide a stable planning target, not a cross-model hard guarantee.
- **A hard character or serialized-byte limit is still allowed.** Exact transport-size fences complement approximate token planning.
- **The durable result is structured.** The final model-facing content is a `PromptNode`. XML is represented natively with `PromptElement`; JSON, TOON, or another textual syntax may be represented by bounded text or grouping nodes. Flattening occurs only at the provider boundary.
- **Output syntax is a prompt-projection decision.** JSON, XML, TOON, or another measured syntax is not a platform or traversal boundary. Production may expose only one syntax.
- **Process isolation and RPC are separate work.** The contracts in this specification are process-local. Future `VisualElementHandle`, `ScopeHandle`, `ResilientCacheHandle`, transport, query-host restart, and coarse-grained remote operations must not be anticipated in the current domain API.

### 2.3 Non-Goals

- Capturing every descendant before planning.
- Making a target survive eviction from its owning `VisualElementStore`, or silently reattaching an unavailable ID to a reconstructed live element.
- Inferring application-specific concepts such as a GitHub feed card, IDE panel, or browser article.
- Guaranteeing exhaustive `Find` results over an unbounded live tree.
- Allowing a `Composite` to masquerade as an actionable UI element.
- Retaining a permanent compatibility layer around the current `VisualContextBuilder` design.

### 2.4 Namespace and Assembly Boundary

Visual Context is a product subsystem rather than a Chat implementation detail or a collection of native interop helpers. Its public and platform-neutral types use:

```csharp
namespace Everywhere.VisualContext;
```

They are implemented by the dedicated `src/Everywhere.VisualContext` project. Do not place these domain-specific contracts in the broad `Everywhere.Abstractions` assembly merely because they are cross-platform, and do not create a second `Everywhere.VisualContext.Abstractions` assembly unless a demonstrated deployment boundary later requires contracts to ship independently from the platform-neutral implementation.

[`PromptNode`](../PromptNode.md) is a genuinely shared model-facing abstraction rather than a Visual Context domain type. Its complete usable foundation lives in the neutral `src/Everywhere.Prompting` project. The prompt tree and document types use `Everywhere.Prompting.Documents`; shared token estimation uses `Everywhere.Prompting`. The assembly includes the serializable node hierarchy, `PromptElement`, prompt collections, `PromptRenderResult`, and the renderer and estimation support required by `PromptNode.ToString()` and `PromptDocument.Render(...)`. Moving only DTO declarations while leaving their required behavior in Core would preserve the same dependency problem through a less explicit boundary.

The intended reference direction is:

```text
Everywhere.Core --------------------+--> Everywhere.VisualContext --> Everywhere.Prompting
Everywhere.Windows -----------------|
Everywhere.Mac ---------------------|
Everywhere.Linux -------------------|
Visual Context tests ---------------+

Everywhere.Core -----------------------------------------------------> Everywhere.Prompting
```

Namespace ownership follows responsibility:

- platform-neutral contracts, Scope, Snapshot, Plan, targets, and prompt construction use `Everywhere.VisualContext`;
- high-level platform implementations use `Everywhere.Windows.VisualContext`, `Everywhere.Mac.VisualContext`, or `Everywhere.Linux.VisualContext`;
- raw COM, P/Invoke, AX, AT-SPI, windowing, and native handle helpers remain under the corresponding platform `Interop` namespace;
- `Everywhere.Prompting` owns the reusable `PromptNode` tree and its format-safe rendering behavior. It references neither Core nor Visual Context;
- `Everywhere.VisualContext` references `Everywhere.Prompting` and returns structured prompt content directly;
- Chat owns history and provider integration, but it owns neither the reusable prompt tree nor the Visual Context domain model.

The existing `Everywhere.Interop.IVisualElement` and `IVisualElementContext` APIs are migration sources, not types to move mechanically. They currently combine property-by-property provider access, eager tree navigation, input, screenshot, selection, and presentation conversion. The replacement `VisualElement` keeps element-centered query, relation enumeration, and image-capture behavior while routing bounded accessibility work through the active `VisualContextScope`. Legacy callers continue to use the old API until they are migrated, after which the legacy surface is deleted.

## 3. Observed Failure Shape

Real visual trees are not merely deep text documents. A typical web feed can contain hundreds of rendered nodes, repeated one-child panels, nested lists, duplicated text and hyperlink names, interactive controls embedded inside descriptive labels, and several core elements that all belong to the same native top-level window.

The current pipeline exhibits the following structural failures:

- multiple observed core paths can render duplicate `TopLevel` entries for the same native root;
- informative-descendant propagation can cause containers with no own semantics to be serialized;
- adjacent-label merging cannot compress nodes that contain semantic hyperlink or button descendants;
- source elements receive IDs before merging, leaving gaps and entries that the agent never saw;
- a merged label keeps the first source ID even though its rendered text describes several sources;
- one large feed or virtualized collection can consume nearly the entire response budget;
- structural JSON fields can dominate the useful content.

The replacement therefore treats compression as a first-class projection of a complex visual range, not only as string concatenation.

## 4. Core Domain Model

### 4.1 Visual Context Snapshot

Snapshot returns a bounded observation forest. The name deliberately avoids `Document`: a visual-context snapshot can be disconnected, incomplete, non-textual, and much larger than the portion observed.

```csharp
namespace Everywhere.VisualContext;

public abstract class VisualElement : IDisposable
{
    public string Id { get; }

    protected VisualElement(VisualElementStore store, string id);

    public VisualElementQueryResult Query(VisualElementQueryRequest request);

    public IVisualElementEnumerator CreateEnumerator(
        VisualElementRelation relation,
        VisualElementQueryRequest request);

    public ValueTask InvokeAsync();

    public ValueTask SetTextAsync(string text);

    public ValueTask FocusAsync();

    public ValueTask SendShortcutAsync(KeyGesture shortcut);

    public Task<IVisualElementCapture> CaptureAsync(CancellationToken cancellationToken = default);

    public void Dispose();

    internal void Release() => ReleaseCore();

    protected abstract Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken);

    protected abstract void ReleaseCore();
}

public sealed class VisualContextSnapshot
{
    public required IReadOnlyList<VisualContextSnapshotNode> Roots { get; init; }
}

public sealed class VisualContextSnapshotNode
{
    // Retained for later actions. Plan and prompt construction must never read platform-backed
    // properties through this reference.
    public required VisualElement Element { get; init; }

    public required VisualElementSnapshot Snapshot { get; init; }

    public VisualContextSnapshotNode? Parent { get; set; }
    public List<VisualContextSnapshotNode> Children { get; } = [];

    public int LocalDistance { get; init; }
    public int GlobalDistance { get; init; }
    public float TraversalPriority { get; init; }
    public long TraversalOrdinal { get; init; }

    public bool IsCore { get; init; }
    public bool IsInteractive { get; init; }

    public List<string> Status { get; } = [];
}

public sealed record VisualElementSnapshot(
    string? Id,
    VisualElementType? Type,
    VisualElementStates? States,
    string? Name,
    string? TextPreview,
    bool HasMoreText,
    PixelRect? Bounds,
    int? ProcessId,
    nint? NativeWindowHandle);
```

Important constraints:

- Known core properties are typed fields, not an unbounded `Dictionary<string, object>`.
- `AvailableFields` and `MissingFields` on the corresponding read result are authoritative; nullable snapshot fields do not by themselves distinguish an unavailable value from a field that was not requested.
- `Element` is the element-centered platform abstraction. Snapshot may call its bounded query and navigation methods while the owning Store has an active compatible Scope. Plan and prompt construction use only `Snapshot` and never perform platform reads.
- Children are ordered. A separate identity map performs deduplication.
- Snapshot nodes do not have final integer IDs.
- Snapshot stores only facts that were safely observed and bounded traversal metadata.
- Snapshot does not claim that an unobserved sibling or descendant does not exist.
- Every publicly visible `VisualElement` has already been accepted by exactly one `VisualElementStore`. A platform may hold a temporary native candidate internally during acquisition, but callers never observe an unowned or detached `VisualElement` and never perform an adoption or ownership-transfer step.

### 4.2 Agent-Facing Targets

The Agent-target index inside `VisualElementStore` contains a discriminated target type rather than only `VisualElement` values:

```csharp
public abstract class VisualTarget
{
    public required VisualTargetCapabilities Capabilities { get; init; }
    public IReadOnlyList<string> Status { get; init; } = [];
}

public sealed class ElementTarget : VisualTarget
{
    public required VisualElement Element { get; init; }
}

public sealed class CompositeTarget : VisualTarget
{
    public required IReadOnlyList<CompositePart> ObservedMembers { get; init; }
    public required IReadOnlyList<VisualTarget> ExposedChildren { get; init; }
    public required bool HasMoreMembers { get; init; }
    public CompositeContinuation? Continuation { get; init; }
}

[Flags]
public enum VisualTargetCapabilities
{
    None = 0,
    Inspect = 1 << 0,
    Navigate = 1 << 1,
    Expand = 1 << 2,
    ReadContent = 1 << 3,
    Find = 1 << 4,
    Invoke = 1 << 5,
    SetText = 1 << 6,
    SendShortcut = 1 << 7,
    Capture = 1 << 8,
    Focus = 1 << 9,
}
```

The code is illustrative. The semantic rules are mandatory:

- `ElementTarget` corresponds to exactly one live element.
- `CompositeTarget` corresponds to multiple observed source elements, a complex subtree, or a contiguous logical member range.
- A Composite is a Planner projection, never a `VisualElementType` returned by a platform.
- A Composite is not directly actionable. Interactive descendants must receive their own `ElementTarget` IDs when exposed.
- An action resolver must validate capabilities and reject a Composite before reaching platform code.
- A publication batch contains only targets that were actually emitted or returned by a query. Hidden and merged source nodes do not consume Agent IDs, although their elements remain Store-owned while Snapshot or a retained Composite still needs them.

### 4.3 VisualElementStore Ownership

`VisualElementStore` replaces the current `ChatContext.VisualElements` and `ResilientCache` combination. One Store has one owning Chat context. A derived subagent may explicitly borrow that Store when it is intended to operate in the same visual-target domain, but unrelated conversations never inherit one implicitly. The owning Chat context must outlive every borrower and is responsible for disposing the Store.

The Store has two related responsibilities:

1. own every `VisualElement`, its platform resources, retention state, and current operation pins; and
2. map monotonically allocated integer IDs to published `ElementTarget` and `CompositeTarget` values.

Its synchronization model is deliberately hybrid rather than one coarse lock or an actor queue:

- the one-active-Scope reference is installed and removed with atomic compare-and-swap operations, and element calls read it without taking a Store-wide lock;
- Scope pins are recorded by the serial operation Scope, while each `VisualElement` atomically combines its pin count with an early-release request so physical release occurs exactly once after the final pin leaves;
- the Store's strong ownership index is concurrent and is not consulted on every scalar query;
- committed Agent targets use an immutable-after-publication copy-on-write state. Resolution reads one atomic snapshot without locking, while publication, eviction, and invalidation serialize only construction and replacement of that snapshot.

No synchronization primitive is held across UIA, AX, screenshot, or other platform work. Snapshot replacement is proportional to the bounded set of retained published targets, not to the size of the source accessibility tree. If that retained set later becomes large enough for copying to matter, the state may move to a structurally shared persistent map without changing the Store API or weakening atomic publication.

Registering a platform element with the Store is not Agent publication and does not consume an integer ID. A newly acquired root or enumerated related element is accepted by the Store before it crosses a public API boundary. Prompt construction later begins a provisional publication batch for only the targets that the Agent will actually see. Republishing an existing retained target reuses its committed ID; a genuinely new target receives the next monotonic ID. A failed or abandoned prompt does not consume IDs, and a committed ID is never silently reused for another target. Consequently, IDs newly allocated by one batch are consecutive, but the IDs visible in a later result need not form a contiguous range.

The Store is the maximum logical lifetime boundary for its elements. A `VisualElement` may survive any number of operation Scopes, but it cannot remain usable after its Store is disposed. Scope completion does not dispose retained elements. Store disposal, explicit element disposal, target eviction, or an unavailable-element decision may request release; physical release is delayed while an active Scope has the element pinned.

`VisualElement.Dispose()` is a public early-release request. It delegates to its Store rather than directly releasing COM, CoreFoundation, GObject, or other platform resources. The Store makes release idempotent, removes or invalidates affected targets, waits for active pins to end, and then calls the element's internal `Release()` entry point. Platform subclasses implement `ReleaseCore()` and release only resources they actually own. They do not modify Store retention or pin state themselves.

Agent-facing status belongs to a particular observation or publication and is not stored on the retained target. Republishing an element creates a new operation-local observation while preserving the committed ID and underlying target identity. Store-internal recency, failure history, pin state, and eviction weights are lifecycle metadata rather than Agent status. A later successful query must not inherit a stale timeout message merely because the Store used that timeout as an eviction signal.

### 4.4 Composite Semantics

`Composite` is the single agent-facing compression concept for both:

1. **content composition**, where fragmented passive nodes jointly express dense readable content; and
2. **subtree encapsulation**, where a mixed visual region contains substantial structural overhead, text, and interactive descendants.

A rendered Composite may expose:

```json
{
  "id": 42,
  "type": "Composite",
  "members": 37,
  "interactive": 4,
  "preview": "A bounded dense projection of the observed region...",
  "children": [],
  "more": true
}
```

`members` describes source members included in the logical projection. `children` contains only independently useful logical or interactive targets exposed in the current result. They are not interchangeable.

A Composite must not eagerly concatenate an unbounded string. It retains ordered bounded parts such as:

```text
CompositePart
|- Source node reference
|- Bounded content slice
|- Separator or structural boundary
|- Traversal ordinal
`- Optional exposed child target
```

### 4.5 Status

`Status` is the single Agent-facing explanation channel for incomplete or degraded observations. Omission is one kind of status rather than a separate flags protocol. Typical sources include child or node limits, provider timeout, incomplete enumeration, traversal deadline, content limits, prompt-budget pruning, unavailable input quiescence, and a provider-level circuit breaker.

Snapshot and Plan append bounded status messages to the closest useful Element, Composite, or root. Related messages may be deduplicated or coalesced before prompt construction. The output does not expose internal timeout counters, retry counters, timestamps, raw exception objects, or a separate field for every missing property merely because those details exist inside the implementation.

Status describes consequences in language useful to the Agent. For example:

```text
Accessibility query timed out; some properties and children may be missing.
The accessibility provider is unresponsive after repeated timeouts; the subtree is incomplete.
Some source elements could not be read; the combined content may be incomplete.
Additional children were not observed within the current query limits.
```

Status does not promise that missing information can be recovered without a new platform read. It is required control information and has higher prompt priority than ordinary preview text or secondary metadata.

## 5. VisualContextScope

### 5.1 Entry Point and Responsibility

`VisualContextService` is a scope factory rather than a stateless read facade. Its primary entry point is asynchronous because showing the overlay, dispatching UI work, and waiting for native hook installation may require asynchronous coordination:

```csharp
await using var scope = await visualContextService.EnterScopeAsync(
    store,
    options,
    cancellationToken);

var result = scope.Read(coreElements, request);
// or: scope.Query(request)
// or: await scope.ExecuteActionsAsync(actions, cancellationToken)
```

There is no `VisualContextService.ReadAsync`. High-level Snapshot, query, and action orchestration are operations of the returned `VisualContextScope`. Element-specific scalar queries, navigation, enumeration, and image capture are invoked on `VisualElement`; the active Scope supplies their bounded transaction and safety context through the owning Store. This keeps the public element model object-oriented without making individual elements own a transaction or operation lifetime.

The scope owns:

- its association with exactly one `VisualElementStore` for the operation;
- a platform read session or transaction created by `IVisualElementContext`;
- a hard total deadline, cancellation source, and monotonic risk counters;
- platform input suppression where supported;
- the visible user notification or screen overlay;
- active injected-input guard state;
- bounded sparse observation and platform-read caches;
- temporary pins for Store-owned elements used by the operation;
- bounded provider outcomes reported back to the Store as retention evidence;
- cleanup of enumerators, platform hooks, transaction state, and overlay resources.

The Scope does not publish Agent IDs and does not own retained elements. Prompt construction opens its own provisional publication batch against the same Store. Ending the Scope releases its temporary pins and operation resources, while any element still retained by the Store remains usable by a later Scope.

The initial process-local implementation permits only one active Scope per Store. This serializes access to the Store's current platform session, pins, and input guard without introducing another ambient-operation identity. If later evidence requires concurrent observation within one visual-target domain, the design must first define how those operation states remain distinguishable; it must not silently make element methods choose an arbitrary Scope.

The foundation API makes the three timeout domains explicit:

```csharp
var options = new VisualContextScopeOptions(
    totalTimeout,
    connectionTimeout,
    transactionTimeout);

public interface IVisualElementContext
{
    ValueTask<IVisualElementQuerySession> CreateQuerySessionAsync(
        VisualElementStore store,
        VisualElementQuerySessionOptions options,
        CancellationToken cancellationToken);
}
```

`VisualContextScopeOptions` uses validated properties rather than positional-record state.
`TotalTimeout` begins before session creation and covers the aggregate scope. `ConnectionTimeout`
bounds establishing communication with a provider when the platform exposes that distinction.
`TransactionTimeout` bounds one provider request after communication exists. Windows UIA maps both
values directly. macOS AX has no separate connection-timeout setting: session setup is charged to
the aggregate deadline, while AX messaging timeout provides the per-request boundary. Unsupported
timeout capabilities degrade to the minimum honest platform contract rather than pretending that
all three values have identical native mappings. The session must retain the supplied cancellation
token for its complete lifetime rather than treating it as a factory-only token.

`VisualElement` is a Store-owned process-local object rather than an opaque identity. It exposes bounded `Query`, `CreateEnumerator`, and image `CaptureAsync` as element-centered operations. `CreateEnumerator` is the only navigation primitive: adding a separate single-step `Move` would duplicate the first `MoveNext()` operation and force timeout, failure, pin, cache, and accounting semantics to be implemented twice. A platform transaction may span many related elements, so it is still created by `IVisualElementContext`, owned by the active Scope, and used internally by platform element implementations. The action resolver validates the published target and its capabilities, then uses the platform action path while a compatible Scope is active.

The scope does not own a complete accessibility-tree snapshot. It may accumulate only the nodes, Composite parts, and continuation descriptors already needed by bounded operations.

### 5.2 Windows Input Guard

The Windows implementation uses the already validated low-level-hook design:

1. show a mouse-through, non-activating overlay informing the user that Everywhere is reading the screen;
2. hide the overlay HWNDs from UI Automation through the existing `UIA_WindowVisibilityOverridden` integration and also filter known overlay HWNDs defensively;
3. install `WH_KEYBOARD_LL` and `WH_MOUSE_LL` on a dedicated message-loop thread;
4. use one stable public Everywhere input magic;
5. stamp Everywhere-generated `SendInput` keyboard and mouse events through `dwExtraInfo`;
6. allow events only while the guard is active and both the injected flag and Everywhere magic match;
7. block physical user input and unrelated injected input while the guard is active;
8. treat physical Escape as scope cancellation;
9. remove hooks and the overlay in `finally` paths.

Hook callbacks must perform only constant-time state checks. They must not log, wait, dispatch UI work synchronously, or acquire contended locks.

All Agent input injection must be centralized so keyboard and mouse paths cannot accidentally omit the fixed magic. The magic is a classifier rather than a security credential or per-Scope identity. UI Automation pattern actions that do not generate low-level input do not require a tag.

The guard must account for keys or mouse buttons that were already down when the scope began, so it does not swallow only the corresponding release and leave a target application with a stuck input state.

### 5.3 Other Platforms and Degradation

Other platforms implement the same semantic contract using their supported event-filtering and accessibility-window facilities. Platform-specific mechanisms require separate validation.

If an input guard cannot be established, the scope may degrade only if the caller permits best-effort reading. The result must expose that quiescence was unavailable; it must not claim that the target UI was protected from user mutation.

### 5.4 Operation Boundary

The Scope ends on completion, Agent action boundary, timeout, cancellation, user Escape, or platform-guard failure. It must not block the user's desktop indefinitely while a model is thinking. Scope completion invalidates the current transaction, enumerators, caches, input guard, and element pins; it does not dispose Store-retained `VisualElement` instances.

The aggregate deadline uses a monotonic clock and is checked before and after session creation,
scalar queries, Enumerator creation, `MoveNext`, and lookahead. Local Enumerator metadata is rejected after Scope completion but does not consume a platform operation. Scope-level platform
operation counting is monotonic but is not itself a traversal limit: the Capturer or Traverser
combines it with node, child, content, and provider-failure costs. This keeps risk policy out of the
platform lifetime abstraction.

Cancellation can stop asynchronous setup and providers that cooperate with the retained scope token. It cannot safely unwind a synchronous native call that is already blocked on its calling thread. The native UIA or AX timeout remains the immediate hard safety boundary for that call; if it eventually returns after the Scope has ended, its late result is discarded and no subsequent operation is allowed in that Scope. Process isolation and restart policy are deliberately outside this refactor.

A provider timeout that returns normally is represented in the partial observation and contributes bounded status at the affected element, relation, or root. Scope timeout is an aggregate observation boundary, not evidence that every in-flight native call was physically interrupted.

An Agent action is itself a UI mutation. A later query may reuse retained target identity where valid, but it starts a new bounded observation rather than claiming the previous state remains current.

### 5.5 Layered Failure Containment

The required containment layers are:

1. the native UIA or AX messaging timeout bounds one dangerous provider operation;
2. `VisualContextScope` bounds aggregate time and prevents further work after its deadline.

Only production evidence of frequent stuck calls justifies adding a same-process worker/watchdog layer. It uses a bounded `Channel` for dangerous native operations and one or more dedicated workers that execute at most one operation at a time. A worker reports progress while idle and at safe points between operations. If its progress stops, the watchdog marks it for retirement and cancels its lifetime token. The token does not abort the in-flight synchronous call: if the call eventually returns, the retired worker discards the late result and exits.

If too many retired workers remain stuck, usable capacity falls below its threshold, or the bounded queue can no longer make progress, the subsystem reports content-free health and queue summaries and declares itself unhealthy. A future isolated query process may make process exit and supervisor restart the outer safety boundary, but that transport and supervision design does not alter the current `VisualElement`, Store, or Scope contracts. Sessions sharing one provider are serialized where the native API requires it, late results are rejected after their owning Scope ends, and the queue never grows without a hard capacity.

This mechanism is not an automatic recovery queue. It does not retry, back off, re-enqueue, or silently move a failed Agent query to another worker. It is a conditional containment mechanism for deciding when the current provider subsystem is no longer trustworthy.

## 6. Platform Query Transactions

The current property-by-property API encourages repeated provider calls and makes total risk difficult to account for. `IVisualElementContext` owns the platform transaction implementation; `VisualContextScope` owns its operation lifetime and safety boundaries; Store-owned `VisualElement` instances expose the element-centered application API.

Do not introduce a separate production `IVisualElementQueryTransactionFactory` service. The platform implementation may use an internal session, cache request, or transaction object, but Snapshot reaches it only through `VisualElement` operations made inside an active Scope.

### 6.1 Semantic Contract

```text
VisualElementQueryRequest
|- RequestedFields
`- MaxTextCharacters

VisualElementQueryResult
|- Element: VisualElement
|- Snapshot
|- AvailableFields
|- MissingFields
`- Failure
```

The aggregate deadline, cancellation, platform-call budget, and child limit belong to `VisualContextScope` and the Capturer rather than being copied into every scalar request. Enumeration options carry the bounded scalar request applied to each yielded element; additional provider paging hints may be added only when a real platform implementation requires them.

The minimum semantic field set includes identity, type or role, states, name, bounded text preview, bounds, process identity, native root handle, and parent/child navigation.

Initial focused, point, and native-window acquisition has no existing element receiver, so it remains a root-acquisition operation of the Scope backed by its query session rather than a legacy process-wide client. `VisualElementLocator` is a short-lived acquisition instruction, not a retained identity. The acquired element is accepted by the associated Store before it is returned. Subsequent scalar queries, relation enumeration, and image capture are invoked on that `VisualElement` while a compatible Scope is active.

`AvailableFields`, `MissingFields`, and `Failure` remain structured internal query facts. They are needed for correct Snapshot decisions, but they are not a requirement to expose a separate Agent-facing field for every diagnostic. A timeout retains every safely observed field, marks the unavailable portion as incomplete, and contributes bounded status to the affected node. It must not be converted into an ordinary `null`, empty string, empty child list, or successful end of enumeration.

When relation enumeration fails, already returned items remain valid partial results. The relation is incomplete, and neither Snapshot nor the prompt builder may represent it as a definitive `HasMore = false`. When identity is known but a scalar query fails, retain a skeleton for that element. When an element cannot be represented safely, attach status to its nearest retained parent or root instead of manufacturing an error element with a platform identity.

The scope tracks repeated timeouts by provider and process identity for its own circuit-breaker decisions. Once the current-scope threshold is reached, it stops issuing further calls to that provider, adds provider-unresponsive status to the applicable root, and leaves other providers eligible while aggregate budget remains. This is not a persistent application health verdict and does not schedule a retry. The Agent may explicitly start a later scope if another attempt is useful.

Platform navigation uses the normal C# enumerator model, extended only with useful bounded-observation metadata:

```csharp
public interface IVisualElementEnumerator : IEnumerator<VisualElementQueryResult>
{
    int Count { get; }
    int Index { get; }
    bool HasMore { get; }
}

public enum VisualElementRelation
{
    Parent,
    Child,
    PreviousSibling,
    NextSibling,
}
```

The exact helper set may evolve during implementation. The required semantics are more important than freezing every property in advance:

- the Enumerator is created by `VisualElement.CreateEnumerator`, is registered with the active Scope, and cannot survive that Scope;
- `Parent` yields zero or one item;
- `Child` yields immediate children in provider order;
- `PreviousSibling` and `NextSibling` start with the adjacent sibling and continue outward in that direction;
- `MoveNext` and lookahead are charged to the same deadline and platform-operation budget, while locally known `Count`, `Current`, and `Index` are not;
- `Index` is the current logical zero-based index when available;
- `Count` is the exact logical count when it is already known without provider work, and `-1` otherwise; reading it never enumerates, materializes, or issues an RPC;
- `HasMore` may use a known count, provider metadata, or a cached one-item lookahead;
- lookahead does not change `Current` or `Index`, and a platform failure is not converted into `HasMore = false`;
- internal enumeration is zero-based; VisualQuery converts its agent-facing 1-based offsets at the boundary;
- disposal is idempotent, and scope teardown disposes any Enumerator that a caller failed to release.

The corresponding element method is:

```csharp
public IVisualElementEnumerator CreateEnumerator(
    VisualElementRelation relation,
    VisualElementQueryRequest request);
```

`IVisualElementQuerySession` is a public cross-assembly platform contract because the OS-specific implementations live in separate projects. It is nevertheless an infrastructure surface: `VisualContextScope` owns it, platform `VisualElement` subclasses use it indirectly through their Store/Scope association, and application code does not pass it around. A platform session or Enumerator must never escape the Scope lifetime. The session factory remains part of the replacement `IVisualElementContext`; no independent transaction-factory service is introduced.

`VisualContextScope` tracks or wraps every returned Enumerator. `Count`, `Current`, and `Index` inspect
only already available local metadata, while `HasMore` and `MoveNext` are charged as logical
platform operations. Enumerator creation and scalar queries are charged as well. This counter does
not pretend to equal a native RPC count: a cache-backed provider may batch several properties into
one RPC, while one logical navigation operation may require provider-internal work.

Child and sibling access must be lazy or indexed. The abstraction must never require materializing all children before Snapshot can apply `MaxChildrenPerNode`. Platforms may cache bounded pages or one-item lookahead inside the transaction.

The current `VisualElementSiblingAccessor` is not used by the new Capturer. It may remain temporarily for existing StrategyEngine callers, then should be removed with the legacy `Everywhere.Interop.IVisualElement` API after those callers migrate to `VisualElement.CreateEnumerator`. Its paired shared-resource lifetime and eager macOS sibling materialization must not be carried into the replacement.

The transaction is not ACID and does not establish a full-tree version. It is a safety and batching boundary around one bounded observation batch.

### 6.2 Windows UI Automation

- Each Runtime worker owns one `CUIAutomation8`; immediately before a dispatched provider operation, configure that worker through `IUIAutomation2.ConnectionTimeout` and `IUIAutomation2.TransactionTimeout`.
- Timeout behavior follows the worker executing the UIA RPC rather than the worker that originally obtained the retained Element. Element identity therefore does not carry worker or client affinity.
- Never share mutable timeout settings across concurrently executing workers. A client-per-worker policy isolates overlapping Scope options without making `CUIAutomation8` part of logical Element identity.
- Use `IUIAutomationCacheRequest` and `*BuildCache` APIs to retrieve requested scalar properties in fewer provider calls.
- Prefer current-element cache scope when a container size is unknown.
- Never issue an unbounded `TreeScope.Children` or `TreeScope.Descendants` cache request before traversal fences can apply.
- Configure each navigation CacheRequest with `TreeScope.Element`; obtain only the immediate parent,
  first child, or adjacent sibling through the corresponding TreeWalker `*BuildCache` method. Lazy
  continuation then advances one sibling at a time.
- Cache identity and requested scalar properties. Use cached ValuePattern as the common scalar text
  capability and truncate the returned preview locally under the UIA transaction timeout, Scope deadline,
  and Runtime containment. Use cached TextPattern followed by `DocumentRange.GetText(maxLength)` for
  documents and other providers that expose ranged content.
- Convert `UIA_E_TIMEOUT` to `TimeoutException` and preserve the original `COMException` as the inner exception.
- Distinguish unavailable element, unsupported property, timeout, and unknown COM/RPC failure.
- Do not collapse every `COMException` into `null` or a default value.

### 6.3 macOS Accessibility

- Use `AXUIElementSetMessagingTimeout` as the native per-message safety boundary. Setting it on the system-wide element changes the process-wide timeout; setting it on an ordinary element applies only to that exact `AXUIElementRef` and must not be assumed to cover another equal reference. A zero value resets the applicable timeout to the system default. An implementation that uses the system-wide setting keeps one stable process policy or serializes configuration changes; overlapping Scopes must not race while rewriting a process-global timeout.
- Treat `kAXErrorCannotComplete` as a broad provider-communication failure rather than claiming that it always proves a precise timeout. Normalize it into the common provider-failure or timeout policy where appropriate, while retaining the raw `AXError` internally.
- Batch scalar attributes with `AXUIElementCopyMultipleAttributeValues`. Without stop-on-error behavior, preserve per-position AX errors so one unsupported attribute does not discard successful values from the same batch.
- Use `AXUIElementGetAttributeValueCount` and `AXUIElementCopyAttributeValues(index, maxValues)` for large array-valued attributes such as children. Do not materialize the complete array merely to apply `MaxChildrenPerNode` locally.
- Where the provider supports it, obtain text length through `AXNumberOfCharacters` and read ranges with `AXUIElementCopyParameterizedAttributeValue`, `AXStringForRange`, and an `AXValue`-wrapped `CFRange`.
- Ordinary string attributes such as title or value have no maximum-length parameter and may be read under the AX messaging timeout before the returned preview is truncated locally.
- Generic sibling traversal is implemented through bounded parent/child enumeration with a cached index hint or a lazy paged scan using `CFEqual`. `AXNextContents` and `AXPreviousContents` are split-view-divider attributes, not generic sibling navigation.
- Convert unavailable elements, unsupported attributes, provider communication failures, and unknown AX failures into the same semantic categories as Windows without discarding platform detail.

The API probe has confirmed these contracts from Apple's declarations, but the scoped AX reader must be implemented and validated on macOS. Cross-compiling the managed P/Invoke layer on Windows cannot verify the runtime CoreFoundation types returned in a mixed-value batch, Create/Copy ownership, equality of independently obtained `AXUIElementRef` values, the effective scope of messaging timeouts, or provider-specific range behavior. A Windows-authored implementation is not considered complete until it passes native macOS probes and controlled TestApp reads under success, unsupported-attribute, mutation, and unresponsive-provider conditions.

### 6.4 Fallback Behavior

If a platform cannot batch a requested field set, it may perform individual reads. All fallback reads remain inside the same deadline, platform-operation budget, requested fields, and content limits. A fallback must never turn one bounded request into an unbounded sequence.

## 7. Phase 1: Snapshot

### 7.1 Responsibility

```csharp
public interface IVisualContextSnapshotter
{
    ValueTask<VisualContextSnapshot> CreateSnapshotAsync(
        VisualContextScope scope,
        IReadOnlyList<VisualElement> coreElements,
        VisualContextSnapshotLimits limits,
        CancellationToken cancellationToken = default);
}
```

Snapshot records facts and traversal metadata by using `VisualElement.QueryAsync` and `VisualElement.CreateEnumeratorAsync` while the supplied Scope is active. Initial locator-based root acquisition remains a Scope operation because it has no element receiver. Snapshot does not bypass the Scope's deadline, session, cache, pin, or failure accounting, and it does not collapse containers, create Composites, allocate output budget, serialize a format, or assign integer IDs.

### 7.2 Preserved Traversal Semantics

The implementation lifts the existing algorithm rather than redesigning relevance:

- core elements remain the highest-priority seeds;
- existing direction weights remain unchanged;
- `TraverseDistance.Reset()` and `TraverseDistance.Step()` remain unchanged;
- current type weighting remains unchanged unless separately reviewed;
- visited elements remain deduplicated within one Snapshot;
- existing allowed directions remain supported by `VisualQueryOperation.Inspect`;
- effective queue priority and dequeue ordinal are recorded for every snapshot node.

Plan reuses this order. It does not introduce a second global importance score.

### 7.3 Convergence Invariant

Every Snapshot-loop iteration must do one of the following:

1. commit one previously unseen node;
2. advance one bounded `IVisualElementEnumerator`; or
3. permanently close one branch.

All budgets are monotonic. Snapshot is bounded by at least:

- total elapsed time measured by a monotonic clock;
- total snapshot nodes;
- maximum observed children per parent;
- total platform calls or transaction operations;
- maximum snapshot text characters per node;
- maximum snapshot text characters or bytes;
- per-provider or per-root timeout count;
- optional depth or edge limits if real providers justify them.

Caller cancellation remains `OperationCanceledException`. A provider timeout normally degrades or closes only the affected branch. The aggregate deadline returns the partial observation with status describing which portions are incomplete.

All Enumerators are disposed on success, duplicate detection, limit exhaustion, timeout, cancellation, and unexpected exception. Scope teardown remains the final safety net.

### 7.4 Extreme Inputs

#### One Element with Huge Text

1. Read lightweight properties before text.
2. Prefer a platform API that accepts a maximum range or length.
3. Store a bounded preview and add status when content is incomplete.
4. Do not retrieve an unbounded string and then tokenize or split it as the primary defense.
5. If the platform cannot read the property safely with a limit, retain a skeleton or another bounded capability instead.

#### One Parent with Huge Child Count

1. Read children through a scoped, lazy or indexed `IVisualElementEnumerator`.
2. Stop after `MaxChildrenPerNode`.
3. Add bounded status explaining that additional children were not observed and retain a continuation descriptor where possible.
4. Do not force a virtualized provider to realize off-screen children.
5. Preserve focused previous/next sibling exploration around a core item.

#### Slow or Broken Provider

- Enforce the per-call platform timeout.
- Count repeated provider timeouts against a failure budget.
- Attach an element query failure to that element when a safe skeleton exists; attach relation failure to the retained parent and mark its descendants incomplete.
- Stop further calls to a provider after its current-scope failure budget is exhausted, and attach provider-unresponsive status to the applicable root.
- Keep unrelated providers and roots eligible until the aggregate deadline expires.
- Preserve only bounded operational status and content-free diagnostics; do not record observed user content in health reports.

## 8. Phase 2: Plan

Snapshot safety and prompt-construction syntax are real boundaries. Visibility normalization, structural compression, Composite creation, and approximate budget admission are one in-memory planning phase.

```csharp
public interface IContextPlanner
{
    VisualContextPlan CreatePlan(
        VisualContextSnapshot context,
        VisualContextPlanningOptions options,
        IContextCostEstimator estimator);
}
```

### 8.1 Normalization Order

Planner performs explicit operations in a fixed order:

1. establish stable root and sibling ordering;
2. coalesce snapshot entries that resolve to the same native top-level root;
3. preserve all core nodes as anchors under the coalesced root;
4. compute `HasRenderableDescendant` independently from `ShouldRenderSelf`;
5. collapse transparent containers while preserving relative child order;
6. identify source ranges and subtrees whose expanded representation has excessive structural cost;
7. create Composite projections and select bounded previews;
8. expose important or interactive descendants as independent targets;
9. add concrete non-platform metadata required by current callers;
10. packetize render work and allocate the approximate target budget.

Running these operations during priority-queue traversal would make the output depend on discovery timing and would conflate platform risk with presentation policy.

### 8.2 Root Coalescing

Multiple core elements may belong to the same platform root. Prompt output must contain that root once. Root identity uses observed platform identity first and process/native-root identity only as a fallback.

Core attachment order remains explicit and deterministic. Coalescing must not merge genuinely disconnected roots that happen to have similar properties.

### 8.3 Transparent Containers

`HasRenderableDescendant` means a node is needed internally to preserve ancestry. `ShouldRenderSelf` means the node has enough independent semantics to consume output.

A Panel with no own name, text, state, action, bounds requirement, or status responsibility normally has:

```text
HasRenderableDescendant = true
ShouldRenderSelf = false
```

Its visible children are promoted without serializing the Panel. These two states must not be represented by one `IsVisible` flag.

### 8.4 Composite Projection

Composite creation is driven by generic structure and estimated render cost, not application-specific recognition.

A candidate may be:

- a sequence of passive content fragments in stable logical order;
- one existing container subtree with excessive expanded cost;
- a contiguous member range inside a large collection;
- a mixed region whose useful preview and important interactive descendants are much cheaper than its full structural representation.

Planner may create a Composite when member count, estimated structural overhead, or content-to-structure ratio crosses configured thresholds. The thresholds are planning policy and must be measurable through tests and telemetry.

Mandatory rules:

- source members remain available through the Composite target;
- member order follows normalized logical order;
- core elements are never silently absorbed without an exposed anchor;
- interactive descendants selected by the existing relevance order receive independent Element targets;
- the Composite itself receives no action capabilities;
- previews are bounded and deterministic;
- one oversized member is split into bounded parts rather than blocking the whole Composite;
- nested Composites are allowed only within a bounded nesting policy;
- Planner never concatenates an unbounded string before applying content limits;
- a Composite preserves or summarizes status from source members whose missing information could make its preview incomplete.

### 8.5 Reuse of Existing Weights

Weighted BFS answers which observed node is more relevant. Planner adds only cost, projection, and admission decisions:

- priority and `TraversalOrdinal` order candidate skeletons and exposed descendants;
- the selected PromptNode builder supplies approximate fragment cost;
- Planner decides which bounded fragment is admitted;
- Planner does not replace the tuned order with a global `priority / cost` score.

Use progressive allocation:

1. retain core skeletons and required query anchors;
2. admit important element and Composite skeletons in existing relevance order;
3. admit bounded Composite previews and element content in relevance order;
4. admit secondary metadata when budget remains;
5. reserve enough control budget for status and continuation instructions.

### 8.6 Approximate Budget Contract

`targetTokenBudget` is an approximate planning target:

- the estimator is stable for one PromptNode projection;
- estimates preserve useful relative costs;
- an optional safety margin absorbs common tokenizer differences;
- a final estimate may be recorded for telemetry;
- no component claims exact compliance with every model tokenizer.

The old names `Exact Target Truncator` and `preciseLimit` must not be retained.

Every render unit also has a hard character or serialized-byte bound. A single huge label, Composite part, or line cannot consume an unbounded response or be skipped after only part of it was emitted.

### 8.7 Multi-Root Scheduling

After root coalescing, most plans have one actual root. A work-conserving hierarchical scheduler prevents one genuine extreme root from starving others:

- the outer scheduler uses Deficit Round Robin across roots;
- each root's inner order remains the existing Weighted BFS order;
- root quantum controls cross-root fairness and is not recomputed from node relevance;
- one active root naturally emits its existing inner order without a special case;
- prompt work is packetized into bounded skeleton, preview, content, metadata, and status fragments;
- virtual rounds advance directly when no head fragment is currently affordable.

### 8.8 Status and Expansion

Planner preserves Snapshot status and may add bounded status for prompt-budget pruning or content limits. Related status may be coalesced, but a planning omission must not disappear merely because no platform exception occurred.

Expandable missing content resolves through a final Composite or Element target. Raw platform IDs must not appear as pretend-agent IDs. A Composite may own the continuation for a pruned member range, avoiding a separate synthetic platform node. Status tells the Agent that more information may exist; the target and continuation describe how to request it.

## 9. Phase 3: Build PromptNode

The prompt builder owns syntax and the estimator used by Plan. It returns structured content from `Everywhere.Prompting.Documents`; it does not call `ToString()` or otherwise flatten the result inside the Visual Context pipeline.

```csharp
public interface IVisualContextPromptBuilder
{
    IContextCostEstimator Estimator { get; }

    VisualContextPromptResult Build(
        VisualContextPlan plan,
        VisualElementStore store);
}

public sealed record VisualContextPromptResult(
    PromptNode Content,
    IReadOnlyList<int> PublishedTargetIds);
```

The exact result shape is illustrative; the mandatory boundary is that model-facing content remains a `PromptNode`, while the corresponding publication batch is committed atomically into the existing `VisualElementStore`. The builder does not return or replace the complete Store or its Agent-target index.

Prompt-builder responsibilities are:

1. reuse committed IDs for retained targets and request monotonic new IDs from a provisional Store publication batch after normalization, Composite creation, and coarse budget admission;
2. assign IDs only to target-bearing prompt nodes that are guaranteed to survive prompt rendering;
3. represent Element and Composite targets using one stable semantic schema;
4. expose Composite `members`, independently exposed `children`, bounded preview, status, and continuation state;
5. preserve invariant formatting and correct escaping through Prompting primitives;
6. commit the `PromptNode`'s exact target batch before the result becomes observable, without exposing partially published IDs.

XML is the native structured projection. Each emitted visual target is a `PromptElement`; stable scalar metadata such as `id`, `type`, bounds, capabilities, and a compact `status` value is represented through validated attributes, while nested targets and bounded content are child nodes. Untrusted text remains inside `PromptElement` so the Prompting renderer performs XML escaping. The builder must not concatenate an XML string with `StringBuilder` and then wrap that string in `PromptText`.

`PromptElement` now treats its validated element name and attributes as structural content of their own. When no child survives rendering it emits a self-closing element, so Visual Context can represent an attribute-only target without zero-width characters, whitespace children, comments, or other placeholder text. Target-bearing skeleton admission remains a Visual Context responsibility: self-closing support does not make an arbitrarily large required tree fit the prompt budget.

Target-bearing skeletons, status, and continuation instructions are required PromptNodes or are admitted before prompt construction. Later `PromptDocument` pruning may shorten or remove optional previews and secondary metadata, but it must not remove a published target, corrupt a continuation, or leave the Store pointing at content the Agent never saw. If the required skeleton alone cannot fit, building fails explicitly and abandons the provisional Store batch instead of silently producing inconsistent identity.

JSON, TOON, or another textual projection may use `PromptText`, `PromptTextChunk`, `PromptGroup`, and `PromptChunk` as appropriate. Textual structured fragments must remain syntactically valid under pruning; a builder may rely on Plan for whole-record admission rather than make punctuation independently removable. A new PromptNode subtype is justified only when it introduces a genuinely new rendering, pruning, or serialization rule.

The prompt estimator must account for container punctuation, nesting, escaping, Composite metadata, and status. It must not assume every node has a standalone fixed cost. `PromptRenderResult.OmittedNodes` remains Prompting render metadata; it is not a second Visual Context omission protocol. Known missing visual information is expressed as status before prompt construction.

`VisualContextDetailLevel` controls semantic field inclusion and preview density. It does not select unrelated serialization formats.

The Agent-target index in `VisualElementStore` is the source of truth for both query and action routing:

```text
Element ID   -> ElementTarget -> capability validation -> platform action
Composite ID -> CompositeTarget -> Inspect / Expand / Find only
```

## 10. Agent Interaction: VisualQuery

### 10.1 Tool Boundary

`get_visual_tree` is replaced by a query tool because the result is bounded, may be a forest, and supports more than tree traversal.

The recommended kernel function name is `query_visual`; the request model is `VisualQuery`.

```csharp
public enum VisualQueryOperation
{
    Inspect,
    Expand,
    ReadContent,
    Find,
}

public sealed record VisualQuery
{
    public required VisualQueryOperation Operation { get; init; }
    public required string Target { get; init; }
    public VisualContextTraverseDirections Directions { get; init; }
    public int Offset { get; init; } = 1;
    public int Limit { get; init; } = 200;
    public string? Query { get; init; }
}
```

Use `operation`, not `action`, to avoid confusing retrieval with UI mutations such as click and set-text.

Every operation returns its model-facing content as `PromptNode`. The tool description tells the Agent that results are bounded, may be incomplete, and describe a frequently changing tree. Compact status is the primary signal for deciding whether to narrow the query, inspect another target, use another perception source, wait, or explicitly try again. VisualQuery performs no automatic retry, delayed retry, retry queue, or hidden provider recovery loop.

### 10.2 Inspect

`Inspect` replaces the structural responsibility of `get_visual_tree`:

- resolve a published Element or Composite ID through the current Chat context's `VisualElementStore`, or acquire a root from a native window handle;
- enter a new Scope associated with that Store through `VisualContextService.EnterScopeAsync`;
- create a bounded snapshot of the requested neighborhood using the requested directions;
- reuse Weighted BFS;
- invoke element-specific reads through `VisualElement` and high-level orchestration through the Scope;
- build a normalized `PromptNode` and atomically publish any returned Element and Composite targets back into the same Store.

### 10.3 Expand

`Expand` reads a bounded member slice from a Composite:

```text
query_visual(
    operation: expand,
    target: 42,
    offset: 1,
    limit: 20)
```

The result follows the same high-level contract as `read_file`:

```text
VisualQuerySlice
|- Unit
|- Offset
|- Items
|- NextOffset
`- HasMore
```

Rules:

- offsets are 1-based logical units;
- limits are clamped to safe bounds;
- every returned item has its own hard content and serialization bound;
- `NextOffset` is calculated from items actually returned;
- a single huge member can be divided into multiple logical units;
- the Agent may deliberately request overlapping offsets to assess changes;
- no fingerprint matching or automatic re-anchoring is performed;
- an unavailable target fails explicitly and is never silently replaced;
- relation failure preserves returned items, emits status, and does not report a definitive `HasMore = false`.

### 10.4 ReadContent

`ReadContent` is reserved for a real Element target with text capability, such as a `Document`, `TextEdit`, or long text element. It does not reinterpret a Composite as a platform Document.

Platforms may support different ranged-text capabilities. Ordinary Value reads cover common scalar text, while document-style providers may expose ranged access; both remain under the provider timeout, Scope deadline, and Runtime containment.

### 10.5 Find

`Find` performs a bounded best-effort search starting from an Element, Composite, or current root. It may search observed members first and perform further bounded traversal when permitted.

An empty result does not prove that no match exists in the complete live application. The tool description must state this premise; the implementation does not construct a global search index or complete snapshot. Provider failure or a limit reached during the search is emitted as operation-local status rather than being overwritten by an ordinary empty result.

### 10.6 Mutation Semantics

VisualQuery does not promise cross-call idempotence. The guarantees are narrower:

- target IDs are never silently retargeted;
- each call is bounded;
- an offset expresses where the Agent wants the next best-effort read to begin;
- unavailable targets fail explicitly;
- the Agent chooses whether to continue, overlap, inspect again, or start a new query;
- starting a new query is the explicit retry boundary; the implementation does not retry a timed-out operation inside the previous scope;
- a VisualContextScope reduces user mutation while the bounded operation is active.

## 11. Determinism

Determinism applies to equivalent snapshots, not to a changing live UI.

- Add a monotonic enqueue sequence as the tie-breaker for equal priority scores.
- Define input ordering for core elements and roots.
- Store children and Composite members in ordered lists.
- Use sibling index followed by traversal ordinal as a deterministic fallback.
- Preserve relative order when collapsing transparent containers.
- Coalesce roots before Composite creation.
- Create Composites only after final logical ordering is established.
- Construct known prompt fields in fixed order and extension fields by stable key order.
- Reuse retained target IDs, then allocate new IDs in final render order from the Store's monotonic sequence.
- Do not depend on incidental `HashSet`, dictionary, or equal-priority queue enumeration.

## 12. Refactoring and Cutover Strategy

This is a clean internal replacement. Compatibility exists only long enough to migrate production callers.

```text
Temporary composition root
|- ChatContext-associated VisualElementStore
|- new VisualContextService.EnterScopeAsync
|- new VisualContextScope
|- platform query session supplied by IVisualElementContext
|- new VisualContextCapturer
|- new ContextPlanner
|- new PromptNode builder and Store publication batches
`- new VisualQuery tool
```

Do not introduce adapters that make a Composite pretend to be a `VisualElement`. Do not add Composite behavior to `BuiltVisualElements`. Do not keep assigning IDs before Plan.

During migration, a thin facade may translate existing automatic-attachment calls into the new pipeline. The facade is removed or reduced to a composition root once the debugger, automatic context attachment, actions, and query tool resolve `VisualTarget` through `VisualElementStore` directly.

The cutover must replace together:

- `get_visual_tree` and its prompt description;
- `ChatContext.VisualElements` lookup assumptions;
- action target validation;
- automatic visual-context attachment;
- debugger prompt rendering;
- status and continuation syntax;
- detail-level behavior;
- statistics fields that assume every ID is an element.

After cutover, delete obsolete DTOs, old merge code, hidden pre-render IDs, and unused JSON/TOON/XML branches. Do not retain them as hypothetical compatibility layers.

## 13. Implementation Practices

### 13.1 Enumeration and Allocation

- In hot Snapshot, Plan, and prompt-construction paths, prefer ZLinq `AsValueEnumerable()` for LINQ-style enumeration where it avoids iterator and intermediate allocation.
- Apply `AsValueEnumerable()` especially to repeated filtering, ordering inputs already stored in lists, projections, and aggregation over snapshot nodes.
- Do not mechanically replace a clear indexed loop or span-based operation when the direct form is faster or easier to reason about.
- Do not expose lazy `IEnumerable` instances across platform transaction or scope lifetimes. Platform navigation uses explicitly owned `IVisualElementEnumerator` instances.
- Materialize only bounded collections whose ownership is explicit.

### 13.2 Disposal

- Every Enumerator, hook, platform transaction, and scope has explicit ownership.
- `VisualElementStore` is the maximum logical lifetime owner of every publicly exposed `VisualElement`; Scope completion alone never releases a retained element.
- `VisualElement.Dispose()` requests early Store release. The Store removes or invalidates published targets, waits for active Scope pins, and invokes `ReleaseCore()` exactly once.
- Platform `VisualElement` subclasses release only their own native resources in `ReleaseCore()` and do not implement a second Store, lease, adoption, or reference-count protocol.
- Image-capture results are independently disposable by their callers and do not extend the element's Store lifetime implicitly.
- Local resources use `using` or `await using` where possible.
- For nullable fields that must be disposed and reset atomically in normal lifecycle code, use `DisposeHelper.DisposeToDefault(ref field)` instead of repeating `field?.Dispose(); field = null;`.
- Dispose remaining priority-queue enumerators on budget exhaustion.
- VisualContextScope teardown runs from `finally` and covers partial initialization.
- Do not retain live platform enumerators between VisualQuery calls.
- Do not use `_ = DoAsync()` for cleanup or background work; use `TaskExtensions.Detach` with an explicit exception policy.

### 13.3 General Hot-Path Rules

- Use a monotonic clock for elapsed-time budgets.
- Keep low-level input-hook callbacks constant-time and allocation-free.
- Keep logging out of hook callbacks and high-frequency traversal loops unless sampled.
- Use invariant formatting for PromptElement attributes and textual prompt projections.
- Avoid a second DTO copy of the entire snapshot forest; `VisualContextPlan` references snapshot nodes and bounded Composite parts.
- Avoid introducing new dependencies when the existing platform and ZLinq utilities are sufficient.

## 14. Verification Strategy

The declarative scenario model, seeded generation, Mock backend, controlled target applications, and execution tiers are specified separately in [Declarative Visual Context Testing Specification](Testing.md).

Add characterization tests before moving the traversal algorithm, then focused tests for the replacement behavior. These tests should use the shared declarative scenarios rather than isolated one-off element mocks where the scenario infrastructure applies.

### Snapshot and Platform Boundaries

- existing Weighted BFS direction, distance, weighting, and deduplication;
- deterministic equal-priority dequeue order;
- provider timeout conversion and branch-level degradation;
- an element timeout retaining safely observed fields and producing node status instead of a successful empty value;
- relation timeout retaining yielded children and never becoming a definitive `HasMore = false`;
- repeated same-provider timeouts opening the current-scope circuit breaker, producing root status, and preventing additional calls to that provider;
- unrelated providers remaining eligible after one provider circuit opens;
- a later scope being able to retry only when the Agent explicitly starts a new query;
- aggregate deadline returning a partial observation;
- huge text read limits without an unbounded intermediate string;
- huge child collections stopping at the per-parent limit;
- Enumerator disposal on every exit path and scope-level cleanup of leaked Enumerators;
- `Count`, `Index`, and `HasMore` behavior for known-count, `Count = -1`, lookahead, mutation, and failure cases;
- fallback property reads remaining inside the transaction budget.

### Planning and Composite Projection

- several core nodes under one native root rendering one TopLevel with ordered anchors;
- genuinely disconnected roots remaining separate;
- `HasRenderableDescendant` not forcing a transparent container to render itself;
- child order surviving container collapse;
- passive content fragments becoming one Composite without eager unbounded concatenation;
- a mixed subtree becoming a Composite while important interactive descendants remain independent Element targets;
- core elements never disappearing inside an unqueryable Composite;
- one oversized Composite member splitting into bounded units;
- nested Composite depth remaining bounded;
- source failure status surviving Composite projection;
- approximate budget allocation using a fake prompt-projection estimator;
- one-root scheduling preserving exact inner weighted order;
- multi-root scheduling remaining work-conserving and preventing starvation.

### Prompt Construction and Identity

- retained targets reusing their existing IDs and newly allocated IDs following the Store's monotonic sequence with no entries for hidden source nodes;
- an Element ID resolving to exactly one Store-owned `VisualElement`;
- a Composite ID resolving only to `CompositeTarget`;
- action routing rejecting a Composite before platform invocation;
- XML being represented as native `PromptElement` nodes rather than a prebuilt XML string;
- required attribute-only visual elements surviving as valid empty or self-closing PromptElements without placeholder text;
- status and continuations resolving through emitted targets and surviving optional-content pruning;
- target-bearing PromptNodes never being pruned after their IDs are registered;
- JSON or TOON prompt fragments remaining syntactically valid under their declared pruning behavior;
- stable field order and PromptElement escaping;
- Store publication and PromptNode being produced atomically;
- an abandoned prompt batch consuming no Agent IDs and exposing no targets;
- committed IDs never being silently reused or retargeted.

### Store Ownership

- every `VisualElement` crossing a public API boundary already belonging to exactly one Store;
- an element remaining usable across successive compatible Scopes while retained by its Store;
- Scope teardown releasing pins and operation resources without disposing retained elements;
- explicit element disposal, eviction, and Store teardown invalidating affected targets and invoking platform release exactly once;
- active Scope pins deferring physical release until the operation exits;
- Store-internal failure and recency metadata not leaking as stale status into a later successful observation.

### VisualQuery and Scope

- `Inspect` preserving requested traversal directions;
- `Expand` enforcing offset, limit, unit bounds, and actual `nextOffset`;
- overlapping offsets being allowed;
- unavailable targets failing without automatic re-anchoring;
- timed-out queries not being retried automatically;
- provider and relation failure status surviving empty `Find` or partial `Expand` results;
- `ReadContent` supporting timeout-bounded Value reads and document-style ranged content;
- `Find` respecting traversal and result limits;
- Windows hooks blocking physical input while allowing correctly tagged injected input;
- unrelated injected input being blocked during the scope;
- physical Escape cancelling the scope;
- partial scope initialization cleaning up hooks and overlays;
- action completion ending the active Scope without invalidating Store-retained element identity by itself.

Tests use normal production entry points and platform-reader fakes. Do not add production constructors or hooks solely for tests.

## 15. Execution Plan

Extraction of the complete reusable PromptNode foundation into `Everywhere.Prompting` is complete and is a prerequisite rather than a remaining Visual Context phase.

1. **Characterize the current algorithm.** Freeze Weighted BFS, distance, direction, visibility, output ordering, Enumerator lifetime, and current failure cases in tests before moving code.
2. **Introduce the assembly, Store, Scope, and element foundations.** Add the `Everywhere.VisualContext` project and namespace, Store-owned abstract `VisualElement`, bounded query and enumeration contracts, `VisualElementStore`, `VisualContextService.EnterScopeAsync`, `VisualContextScope`, snapshot models, and `VisualTarget` without cutting over production callers. The declarative Mock subclasses the production element abstraction while temporarily exposing the legacy interface from the same underlying scenario node for characterization.
3. **Implement platform-scoped element operations.** Let each `IVisualElementContext` supply its platform query session. Implement `VisualElement.Query`, `CreateEnumerator`, and `CaptureAsync` over the active Scope, then add bounded field requests and enumeration, Windows UIA timeout/cache support, macOS batching/indexing, fallback rules, and semantic exception conversion.
4. **Implement the scope guard.** Centralize the Windows overlay, low-level hooks, active guard state, fixed-magic Agent input injection, deadlines, cancellation, and cleanup in the platform portion of `VisualContextScope`.
5. **Introduce `VisualContextSnapshot` and migrate Snapshot.** Lift the existing Weighted BFS behind `VisualElement.QueryAsync` and `CreateEnumeratorAsync` calls made inside an active Scope, with time, node, child, platform-operation, content, and provider-failure fences.
6. **Build the new Planner.** Implement root coalescing, separate descendant/self visibility, transparent-container collapse, Composite projection, progressive allocation, status propagation, and DRR scheduling.
7. **Build the PromptNode projection and Store publication integration.** Use PromptElement's self-closing structural semantics for attribute-only targets, pair prompt construction with its estimator, use native PromptElements for XML, reserve target IDs only after the final plan is known, and atomically commit successful publication batches into the persistent Store.
8. **Implement `query_visual`.** Add Inspect, Expand, ReadContent, and Find on `VisualContextScope.Query` with bounded best-effort semantics and offset-based continuation.
9. **Migrate actions and callers together.** Replace `ChatContext.VisualElements` and `ResilientCache` with `VisualElementStore`; update automatic attachments, StrategyEngine sibling queries, action validation, debugger output, statistics, prompts, and detail settings to use `VisualElement` operations and Store-resolved `VisualTarget` values.
10. **Cut over and delete legacy code.** Remove `get_visual_tree`, `VisualElementSiblingAccessor`, pre-render ID assignment, old DTOs, obsolete merge logic, and unused render paths after the replacement passes production-entry tests.
