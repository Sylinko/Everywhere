# Visual Context Architecture

## 1. Architectural Axes

The architecture separates four questions:

| Type | Question | Typical lifetime |
|---|---|---|
| `IVisualElementBackend` | Which platform acquires roots and supplies process-shared native facilities? | Application or future query-host process |
| `VisualContext` | Which identities, ownership batches, and Agent targets belong to this conversation? | Owning `ChatContext` |
| `VisualElementRetention` | Which real owner currently keeps a set of elements alive? | Attachment, Enumerator, Snapshot, or Agent turn |
| `VisualTargetTurn` | Which published and looked-up targets belong to one Agent turn? | Current turn, then historical retention |

Native execution and native-object lifetime are deliberately independent. A timeout answers how long a provider call may block; a retention answers why a canonical element must remain usable after that call returns.

## 2. Object Graph

```text
IVisualElementBackend  <---- shared singleton
|- platform client and fixed timeout policy
|- platform TreeWalker and other shared native services
|- locator + resolution root query
`- never retains Contexts, Retentions, or Elements

VisualContext A  <---- ChatContext A; calls are serialized
|- identity maps (canonicalization only, no independent ownership)
|- explicit retention batches
|- one active VisualTargetTurn
`- completed turns ordered oldest -> newest

VisualContext B
|- independent identity and Agent-ID domain
`- contains no platform services

VisualElement A
|- belongs immutably to VisualContext A
`- uses the shared Backend for concrete platform behavior
```

A derived Agent constructs another Context and therefore has an independent identity and target namespace. Reusing a platform identity in another chat creates a distinct high-level element even when both elements use the same Backend and native RuntimeId.

## 3. VisualElement Backend

`IVisualElementBackend` is the long-lived platform entry point. Its public surface contains one query for acquisitions that have no existing element receiver:

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

The Locator identifies the source sampled when the call executes, or explicitly supplies no anchor through `Default`. Resolution independently selects the direct element, nearest TopLevel, or containing Screen. With a default locator, the same Resolution selects the platform-default object at that level rather than implicitly sampling focus, pointer, or foreground state. The concrete Backend owns reusable platform resources such as the Windows UI Automation client and TreeWalker and releases those resources when the application or query-host shuts down. It does not create Contexts. The supplied retention is the single destination argument: `retention.Context` selects the identity and ownership domain into which a successful root is canonicalized before exposure.

The Backend does not own conversation identities, Agent IDs, traversal state, output budgets, current-turn lifetime, a worker pool, a custom scheduler, a `SynchronizationContext`, or an execution Scope. It must not store a Context, Retention, or Element in a child collection, identity table, event callback, or another reverse reference. Ordinary element calls remain implemented by the concrete `VisualElement`.

The production Backend is normally one dependency-injection singleton. `ChatContext` creates and owns its `VisualContext` directly, including after deserialization and for derived Agents; Contexts are not DI services. Multiple Contexts may use the shared native client concurrently where the platform supports it. The Windows evidence currently supports one immutable-policy `CUIAutomation8` client shared across calls; element identity is independent of the client that returned a COM pointer.

## 4. VisualContext

`VisualContext` is the platform-neutral aggregate root for one Agent-visible visual domain. It owns or coordinates:

- backend-qualified platform identity maps;
- every active `VisualElementRetention` batch;
- one current Agent turn;
- an ordered history of completed turns;
- monotonically allocated Agent target IDs;
- provisional, atomic publication;
- promotion of a historical lookup into the current turn;
- whole-turn eviction.

`VisualContext` is sealed and has no platform subclass or native-service reference. Root acquisition belongs to `IVisualElementBackend`; provider behavior belongs to concrete `VisualElement` implementations. The Context does not own provider timeout configuration, traversal, PromptNode rendering, screenshot buffers, or input overlays.

The application contract is that calls mutating one Context are serialized. Consequently the current implementation uses ordinary dictionaries and linked lists rather than locks or concurrent collections. No Context lock is ever held across UIA, AX, capture, or input work because no such lock exists.

One `ChatContext` strongly owns its `VisualContext` from construction, including direct construction, deserialization, and derived-Agent construction. Normal chat switching may let that aggregate become unreachable and rely on GC's lazy reclamation; explicit Context disposal remains an early-release path for short-lived tools, tests, and shutdown. Context disposal abandons the active turn, releases all completed turns and other retentions, clears identity maps, and makes every no-longer-owned element unusable. It never disposes Backend-owned native clients.

Managed cycles inside the aggregate are harmless when no GC root reaches them. The required directions are `ChatContext -> VisualContext`, application DI -> Backend, and `VisualElement -> VisualContext + Backend`; the shared Backend, native client, static callback, or observer must never point back to a Context or Element. Durable native wrappers have finalizers as leak fallbacks, so lazy aggregate collection eventually releases COM/AX references even when the optional early-disposal path was not used.

## 5. VisualElementRetention

`VisualElementRetention` is an explicit strong-ownership batch. It is intentionally simple:

- one Context creates it;
- it retains each canonical element at most once;
- it is independent of native execution and timeout policy;
- disposing it releases the whole batch;
- an element shared by another batch remains alive through that other owner.

The identity entry carries a count of retention batches, not a count of queries, native pointers, property reads, or local variables. This gives the required reference-counting precision without asking every temporary managed reference to participate.

Typical owners are:

| Owner | Why it retains | Release point |
|---|---|---|
| Chat attachment or pointer selection | The user-visible attachment remains addressable | Attachment replacement/removal or Context disposal |
| Relation Enumerator | `Current` and previously yielded canonical results must survive while the Enumerator is used | Enumerator disposal |
| `VisualContextSnapshot` | Prompt projection and publication need action/query handles after traversal | After target publication/handoff |
| `VisualTargetTurn` | The Agent may query or act on published IDs | Whole-turn eviction or Context disposal |

Ownership transfer follows an add-before-release rule: first retain elements in the destination owner, then dispose the source owner. This prevents the identity entry from reaching zero between phases.

Operation-level owners are deterministic even though the enclosing chat aggregate may be collected lazily. In particular, the visual scan-effect queue starts consuming only after successful production completes; an abandoned scope releases its retention synchronously, while a completed scope owns its queued elements until the bounded asynchronous drain finishes. This avoids concurrent retention mutation between Snapshot traversal and its visual observer.

## 6. Active Identity Incarnations

An identity map canonicalizes one backend-qualified platform identity only while at least one retention owns it:

```text
native candidate
    -> identity lookup
       |- active entry: retain existing VisualElement, release candidate
       `- miss: attach candidate, retain it, publish canonical element

last retention released
    -> remove exact map entry
    -> release concrete native resource once
    -> incarnation ends
```

The map itself is not a cache and does not prolong lifetime. While an entry is active, equal identities resolve to the same managed `VisualElement` even when UI Automation returns different COM pointers. After the final owner releases it, a later equal RuntimeId is a new incarnation. It may receive a new Agent target ID; old Agent IDs are never silently rebound.

This boundary matches what the system can honestly guarantee. It does not depend on COM pointer equality, RCW reuse, process-wide permanence of RuntimeId, property fingerprints, or heuristic resurrection.

## 7. VisualElement

`VisualElement` is a canonical object-oriented platform element. Its public behavior is receiver-centered:

```csharp
public abstract class VisualElement
{
    public string Id { get; }
    public virtual VisualElementQueryResult Query(VisualElementQueryRequest request);
    public virtual IVisualElementEnumerator CreateEnumerator(VisualElementRelation relation, VisualElementEnumerationOptions options);
    public virtual void Invoke();
    public virtual void SetText(string text);
    public virtual void Focus();
    public virtual void SendKeyGesture(KeyGesture keyGesture);
    public virtual Task<IVisualElementCapture> CaptureAsync(CancellationToken cancellationToken = default);
}
```

Each method validates that the canonical incarnation is still retained, calls a concrete Core method directly, and converts known platform exceptions. Shared action template code stays in the base type; platform behavior stays in the concrete element. A Screen element may directly reject unsupported actions.

`VisualElement` deliberately does not implement `IDisposable`. A shared canonical object cannot interpret one caller's `Dispose` as permission to invalidate every other owner. Deterministic release still exists: disposing the final retention invokes the element's internal `ReleaseCore` exactly once. Native operation-local values continue to use ordinary `using`, and durable interop references retain finalizers only as leak fallbacks where appropriate.

Relations form a logical platform graph rather than one provider tree. A UIA top-level window may return a Win32 Screen parent; a Screen may return UIA windows as children. The origin element defines the edge and propagates its immutable Context and Backend; the Context identity maps canonicalize the returned concrete element.

## 8. Agent Turns and Historical Retention

`BeginTurn` creates the only active `VisualTargetTurn`. Publication and successful historical lookup add targets to that turn and retain any underlying element once.

`Complete` transfers the turn into ordered history. Disposing an incomplete turn abandons it. Completed turns are evicted oldest-first as indivisible units:

```text
turn 1: A B C -----------+
turn 2:   B   D ---------+--> B stays alive until both owning turns leave
turn 3:       D E -------+

TrimRetainedTurns(2)
    -> release all of turn 1
    -> A and C may die
    -> B survives through turn 2
```

This implements conversational recency rather than a flat per-element LRU. Completion automatically trims the oldest whole turns against two policy limits: by default at most eight completed turns and a soft maximum of 2048 distinct retained targets. The newest completed turn is preserved even when it alone exceeds the target limit, because immediately invalidating the result just returned to the Agent would violate the lookup contract.

These are logical-count bounds, not an exact managed/native byte limit. One target may own a native proxy or variable-size Composite metadata; one newest turn may exceed the soft target limit; active attachments, the current incomplete turn, Snapshot traversal, Enumerators, and visual-effect queues have independent retentions. Those operation-level owners must therefore still be disposed promptly. The policy bounds accumulated historical lookup state in a long-lived chat without pretending to calculate tokenizer- or provider-specific memory precisely.

When `TryGetTarget` finds a target in history during an active turn, it promotes that target into the active turn before returning it. Thus an Agent's successful reuse extends the target's lifetime into the current turn.

## 9. Publication

`VisualTargetPublicationBatch` assigns provisional IDs against the active turn. Abandoning a batch consumes no ID. Commit verifies that publication state has not changed, then installs all targets atomically and advances the monotonic counter.

Existing retained targets reuse their Agent ID. New IDs are never reused within a Context, including after turn eviction. Element targets are matched by their active canonical identity; Composite targets are logical objects with their own publication identity.

## 10. Snapshot Ownership

`VisualContextSnapshot` owns the retention produced by graph traversal. Its nodes contain bounded facts plus canonical element handles for later publication. Merged PromptNode projection performs no further platform reads.

Before disposing a Snapshot, every Agent-visible element that must survive is added to the current turn or another destination retention. Snapshot disposal then releases elements that were observed but not published. This keeps large transient traversals out of long-term history.

## 11. Safety Boundaries

The current safety layers are intentionally coarse and honest:

1. the platform's native per-call timeout bounds a UIA/AX RPC where supported;
2. Traverser bounds the aggregate operation count, elapsed duration, child expansion, provider failures, and result size;
3. status records partial failure for the Agent;
4. future process isolation can terminate and restart the entire Automation query host if a platform boundary proves insufficient.

An in-process worker cannot terminate a synchronous RPC that never returns. Watchdog observation alone therefore does not create a stronger containment boundary. Worker, dispatcher, scheduler, SynchronizationContext, operation pin, and Scope machinery are not part of the current design.

An optional UI overlay and input guard may still surround a user-visible read or action, but it is orchestration/UI policy. It does not own native clients or elements and must not be confused with a transaction or immutable platform snapshot.

## 12. Process Isolation

The current object model is process-local. A future isolated query host may expose coarse-grained `VisualElementHandle`, Context/registry handles, snapshot, screenshot, action, and restart operations. Those transport types are not current `VisualElement` responsibilities and must not distort the in-process API before that boundary exists.
