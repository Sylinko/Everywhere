# Visual Context Specification Overview

## 1. Purpose

Visual Context is the platform-neutral subsystem that lets an Agent inspect, query, and act on a frequently changing logical visual graph without assuming that the complete graph can be observed safely. Accessibility trees are its main input, but a platform may compose them with monitor, window, capture, or other native topology.

The numbered chapters are the current specification. [07-Migration](07-Migration.md) records the remaining platform and verification work; historical designs live in version control and are not an active source of truth.

## 2. Chapter Order

1. **Overview** — goals, premises, boundaries, terminology, and the document map.
2. **[Architecture](02-Architecture.md)** — platform Backend, conversation Context, ownership batches, Agent turns, and their lifetimes.
3. **[Element and Target Model](03-ElementModel.md)** — `VisualElement`, bounded query results, Enumerators, Agent targets, Composites, status, identity, and publication.
4. **[Platform Backend and Native Services](04-PlatformRuntime.md)** — root acquisition, native clients, platform timeouts, failure conversion, platform-internal graph composition, and input guards.
5. **[Snapshot Pipeline](05-SnapshotPipeline.md)** — Snapshot, final-text construction, convergence, compression, budget allocation, and determinism.
6. **[VisualQuery](06-VisualQuery.md)** — the Agent-facing query contract, continuation, mutation semantics, and action routing.
7. **[Migration](07-Migration.md)** — current-to-target mapping, staged cutover, and deletion criteria.
8. **[Verification](08-Verification.md)** — acceptance requirements and links to the declarative test infrastructure.
9. **[Final Text Allocation](09-FinalTextAllocation.md)** — final-string results, progressive Root/body allocation, and publication before return.
10. **[Query and Scan Images](10-QueryAndScanImages.md)** — Context-bound queries, optional capture preparation, and image-only animation ownership.

Supporting specifications:

- [Declarative Visual Context Testing Specification](Testing.md) defines scenario/seed generation, Mock and real TestApp backends, mutation, unresponsive-provider controls, and execution tiers.
- [PromptNode](../PromptNode.md) defines the reusable model-facing prompt tree and renderer behavior.
- [Process Isolation](../ProcessIsolation/README.md) defines role startup, RPC resources, Host replacement, peer verification, and Windows service-mode integration.

## 3. Current Architecture in One View

```text
Main
`- ChatVisualState / HostedVisualContext
   |- current Automation Host connection
   |- RemoteVisualContext and remote resource handles
   `- target-validity state across Host replacement

Automation Host connection
|- IVisualElementBackend (connection-owned platform service)
|  |- native accessibility client and fixed timeout policy
|  `- root acquisition and cross-provider graph composition
`- VisualContext resources
   |- platform identity maps and explicit retention batches
   |- current and completed Agent turns
   |- Context-serialized operation queue
   `- VisualElement instances and native resources

Snapshot -> build and validate final text -> atomic target publication -> RPC response
```

The decisive separation is:

- **execution safety** comes from the Automation Host process boundary, native platform timeout, and aggregate traversal limits;
- **logical lifetime** comes from explicit strong ownership batches;
- **Agent lifetime** comes from current-turn ownership followed by whole-turn historical retention and eviction;
- **identity** is canonical only while at least one real owner retains that platform identity.

Inside the Automation Host, root acquisition is the only operation without an existing element receiver. It enters through the connection-owned Backend with a caller-created `VisualElementRetention`; that retention alone selects the destination Context. After acquisition, the concrete element propagates the same Context and Backend through every relation result. Main invokes coarse Context operations through remote resource handles and never receives a native `VisualElement`.

Main also owns a platform Backend for narrow UI services that must run with the interactive application, including screenshot selection and selected-text detection. Those services use transient local Contexts and copied results; they do not publish chat target IDs or replace the Automation Host boundary for Agent-visible queries and actions.

There is no native-call worker pool, Dispatcher, custom `TaskScheduler`, `SynchronizationContext`, operation pin, or execution Scope. Those mechanisms were explored and removed because they could not terminate a synchronous native RPC. Each Host-side Context now has one ordinary Channel consumer to serialize complete Context operations; process termination is the containment boundary when a native call ignores its platform timeout.

## 4. Pipeline

The implementation pipeline has two phases:

1. **Snapshot** is the only phase allowed to read live platform visual state. It preserves Weighted BFS and returns a bounded, partial observation plus explicit status.
2. **Build** performs pure in-memory normalization, transparent-container collapse, Composite projection, progressive budget allocation, and final-text validation before target publication. PromptNode remains an internal syntax mechanism, not a deferred Chat-side allocation strategy.

`VisualQuery` is the Agent-facing boundary over these phases. It replaces the assumption that one eager `get_visual_tree` call can describe the complete application.

## 5. Goals

1. **Bound platform work.** A stalled provider, one enormous text element, or hundreds of thousands of children must produce a partial result or explicit failure rather than unbounded work.
2. **Preserve the tuned relevance algorithm.** Weighted BFS, `TraverseDistance`, direction weights, type weights, core priority, and visited-element deduplication remain the canonical observation order.
3. **Compress without losing queryability.** Fragmented content and structurally expensive regions may become `Composite` targets while useful members and interactive descendants remain inspectable.
4. **Keep identity honest.** An Element ID resolves to one retained `VisualElement`; a Composite ID resolves to a logical `CompositeTarget` and never aliases a convenient source element.
5. **Support bounded follow-up reads.** Structural member paging uses 1-based offsets; text paging uses zero-based UTF-16 offsets. Clamped limits, structural `moreText`, and the current page's `next` keep discovery and continuation separate.
6. **Keep degradation explicit.** Missing fields, limits, timeouts, incomplete enumeration, unavailable input quiescence, and unresponsive providers appear as bounded status on the affected target or root.
7. **Remain deterministic for equivalent observations.** Equivalent snapshot facts, options, and initial publication state produce the same ordering, projections, status, and IDs.
8. **Remove legacy entanglement.** The final implementation deletes the old DTO, pre-render ID allocation, detail-level format selection, and legacy `get_visual_tree` contract.

## 6. Accepted Premises

- The visual tree may be arbitrarily large and may change during every read.
- A platform graph may combine several native mechanisms. Windows already combines Win32 Screen elements with UI Automation windows. A relation may return a different concrete element implementation from its origin.
- The Automation Host serializes calls that mutate one `VisualContext` through that Context resource's operation queue. Its dictionaries therefore do not need locks merely to defend against hypothetical callers.
- Inside the Host, element queries, relations, and actions are synchronous object operations. They call the provider directly and use its configured native timeout as the per-call safety boundary. Main sees asynchronous coarse-grained RPC operations.
- Native timeout does not bound a complete traversal. Snapshot/Traverser must separately enforce elapsed-time, operation, child, failure, and output budgets.
- A read is best effort. An overlay may reduce user-driven mutation, but it is not a tree lock, immutable snapshot, native transaction, or lifetime owner.
- Continuation is Agent-directed. The implementation does not use fingerprints, similarity matching, hidden retry, or automatic re-anchoring to pretend a live tree is stable.
- Token counts are approximate because model tokenizers differ. A projection-specific estimator guides Plan; exact character or serialized-byte fences remain valid transport protections.
- The durable model-facing result is final text. Internal construction uses `PromptCompactElement`, an intentionally XML-like but non-XML node with compact scalar attributes and sparse valueless flags. Safe delimiter-free values may omit quotes. Syntax belongs to the prompt builder and Renderer rather than platform traversal.
- Automation process isolation is an outer containment boundary. The process-local element model remains free of transport concerns; Main exposes only remote Context/anchor/picker handles and copied result contracts.

## 7. Non-Goals

- Observing every descendant before planning.
- Keeping a target usable after every real owner and retained turn releases it.
- Silently reconstructing an unavailable element and reattaching an old Agent ID.
- Inferring application-specific concepts such as a feed card, IDE panel, or article.
- Guaranteeing exhaustive `Find` results over an unbounded live tree.
- Letting a `Composite` masquerade as an actionable accessibility element.
- Making `VisualElement` itself an independently disposable ownership token.
- Letting transport resource IDs or RPC DTOs become native element identity.
- Retaining a permanent compatibility layer around `VisualContextBuilder` or legacy `Everywhere.Interop.IVisualElement`.

## 8. Namespace and Assembly Boundaries

Platform-neutral element contracts and implementation live in `Everywhere.Automation`. Process transport contracts live in `Everywhere.ProcessIsolation`; Main-side remote Contexts and the Automation Host session live in `Everywhere.Core.ProcessIsolation.Automation`. Platform implementations live in `Everywhere.Windows.Automation`, `Everywhere.Mac.Automation`, and corresponding future assemblies. Raw ABI, COM, P/Invoke, AX, AT-SPI, and native-handle helpers remain in platform Interop namespaces. `Everywhere.Prompting` owns reusable `PromptNode` construction and rendering.

The intended dependency direction is:

```text
Everywhere.Core ------------+--> Everywhere.ProcessIsolation
                            +--> Everywhere.Automation --> Everywhere.Prompting
Everywhere.Windows ---------+
Everywhere.Mac -------------+
Everywhere.Linux -----------+
Automation tests -----------+
```

## 9. Observed Failure Shape

Real application trees contain repeated one-child panels, nested lists, duplicated names, fragmented text, interactive controls embedded in descriptive content, virtualized collections, and multiple core elements under one native root. The legacy pipeline consequently exhibits duplicate roots, syntax-heavy output, preallocated ID gaps, dishonest merged identities, unbounded collections, and repeated provider calls with poorly bounded aggregate risk.

The replacement treats observation, compression, identity, publication, platform execution, and Agent continuation as separate responsibilities.

## 10. Source-of-Truth Discipline

- [02-Architecture](02-Architecture.md) is authoritative for ownership and lifetime.
- [03-ElementModel](03-ElementModel.md) is authoritative for element, target, publication, and status semantics.
- [04-PlatformRuntime](04-PlatformRuntime.md) is authoritative for native execution and failure containment.
- [05-SnapshotPipeline](05-SnapshotPipeline.md) is authoritative for traversal, planning, budgeting, and prompt construction.
- [06-VisualQuery](06-VisualQuery.md) is authoritative for Agent-visible query semantics.
- [07-Migration](07-Migration.md) describes temporary implementation state and must not weaken target contracts.
- [08-Verification](08-Verification.md) and [Testing](Testing.md) define acceptance evidence; tests do not create production-only hooks or distort the architecture.
