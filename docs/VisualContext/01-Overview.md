# Visual Context Specification Overview

## 1. Purpose

Visual Context is the platform-neutral subsystem that lets an Agent inspect, query, and act on a frequently changing logical visual graph without assuming that the complete graph can be observed safely. Accessibility trees are its main input, but a platform may compose them with monitor, window, capture, or other native topology.

The original monolithic [Visual Context Refactoring Specification](Refactor.md) is retained as a historical design record. The numbered chapters are the current specification. When they disagree, the numbered chapters and the current implementation take precedence; [07-Migration](07-Migration.md) records unfinished cutover work.

## 2. Chapter Order

1. **Overview** — goals, premises, boundaries, terminology, and the document map.
2. **[Architecture](02-Architecture.md)** — platform Backend, conversation Context, ownership batches, Agent turns, and their lifetimes.
3. **[Element and Target Model](03-ElementModel.md)** — `VisualElement`, bounded query results, Enumerators, Agent targets, Composites, status, identity, and publication.
4. **[Platform Backend and Native Services](04-PlatformRuntime.md)** — root acquisition, native clients, platform timeouts, failure conversion, platform-internal graph composition, and input guards.
5. **[Snapshot Pipeline](05-SnapshotPipeline.md)** — Snapshot, Plan, Build PromptNode, convergence, compression, budget allocation, and determinism.
6. **[VisualQuery](06-VisualQuery.md)** — the Agent-facing query contract, continuation, mutation semantics, and action routing.
7. **[Migration](07-Migration.md)** — current-to-target mapping, staged cutover, and deletion criteria.
8. **[Verification](08-Verification.md)** — acceptance requirements and links to the declarative test infrastructure.

Supporting specifications:

- [Declarative Visual Context Testing Specification](Testing.md) defines scenario/seed generation, Mock and real TestApp backends, mutation, unresponsive-provider controls, and execution tiers.
- [PromptNode](../PromptNode.md) defines the reusable model-facing prompt tree and renderer behavior.
- [`temp.md`](../../temp.md) records only unresolved implementation gaps and native-validation TODOs.

## 3. Current Architecture in One View

```text
process / dependency injection
`- IVisualElementBackend (shared platform singleton)
   |- native accessibility client and fixed timeout policy
   |- root acquisition and cross-provider graph composition
   `- never retains a VisualContext, Retention, or VisualElement

ChatContext
`- VisualContext (conversation identity, lifetime, and Agent-target domain)
   |- platform identity maps
   |- current Agent turn
   |- completed-turn history
   `- explicit VisualElementRetention owners
      |- attachment or pointer selection
      |- Enumerator while it exposes results
      |- VisualContextSnapshot
      `- current or retained Agent turn

VisualElement
|- readonly owning VisualContext and platform Backend reference
|- synchronous Query / enumeration / actions
|- asynchronous pixel Snapshot only where the capture API requires it
`- concrete platform behavior and native resource release

Snapshot -> Plan -> Build PromptNode -> atomic target publication
```

The decisive separation is:

- **execution safety** comes from the native platform timeout, aggregate traversal limits, and eventually process isolation;
- **logical lifetime** comes from explicit strong ownership batches;
- **Agent lifetime** comes from current-turn ownership followed by whole-turn historical retention and eviction;
- **identity** is canonical only while at least one real owner retains that platform identity.

Root acquisition is the only operation without an existing element receiver. It enters through the singleton Backend with a caller-created `VisualElementRetention`; that retention alone selects the destination Context. After acquisition, the concrete element propagates the same Context and Backend through every relation result.

There is no current in-process worker pool, Dispatcher, custom `TaskScheduler`, `SynchronizationContext`, watchdog, operation pin, or execution Scope. Those mechanisms were explored and then removed because they could not terminate a synchronous native RPC and were not required by the actual serialized call path.

## 4. Pipeline

The implementation pipeline has three phases:

1. **Snapshot** is the only phase allowed to read live platform visual state. It preserves Weighted BFS and returns a bounded, partial observation plus explicit status.
2. **Plan** is pure in-memory normalization, root coalescing, transparent-container collapse, Composite projection, and approximate budget allocation.
3. **Build PromptNode** owns output structure, escaping, cost estimation, late target-ID publication, and the final structured `PromptNode` result.

`VisualQuery` is the Agent-facing boundary over these phases. It replaces the assumption that one eager `get_visual_tree` call can describe the complete application.

## 5. Goals

1. **Bound platform work.** A stalled provider, one enormous text element, or hundreds of thousands of children must produce a partial result or explicit failure rather than unbounded work.
2. **Preserve the tuned relevance algorithm.** Weighted BFS, `TraverseDistance`, direction weights, type weights, core priority, and visited-element deduplication remain the canonical observation order.
3. **Compress without losing queryability.** Fragmented content and structurally expensive regions may become `Composite` targets while useful members and interactive descendants remain inspectable.
4. **Keep identity honest.** An Element ID resolves to one retained `VisualElement`; a Composite ID resolves to a logical `CompositeTarget` and never aliases a convenient source element.
5. **Support bounded follow-up reads.** Large content, collections, and Composites use 1-based offsets, clamped limits, `nextOffset`, and `hasMore` semantics similar to `read_file`.
6. **Keep degradation explicit.** Missing fields, limits, timeouts, incomplete enumeration, unavailable input quiescence, and unresponsive providers appear as bounded status on the affected target or root.
7. **Remain deterministic for equivalent observations.** Equivalent snapshot facts, options, and initial publication state produce the same ordering, projections, status, and IDs.
8. **Remove legacy entanglement.** The final implementation deletes the old DTO, pre-render ID allocation, detail-level format selection, and legacy `get_visual_tree` contract.

## 6. Accepted Premises

- The visual tree may be arbitrarily large and may change during every read.
- A platform graph may combine several native mechanisms. Windows already combines Win32 Screen elements with UI Automation windows. A relation may return a different concrete element implementation from its origin.
- The application serializes calls that mutate one `VisualContext`. Its dictionaries therefore do not need locks merely to defend against hypothetical callers.
- Element queries, relations, and actions are synchronous object operations. They call the provider directly and use its configured RPC timeout as the per-call safety boundary.
- Native timeout does not bound a complete traversal. Snapshot/Traverser must separately enforce elapsed-time, operation, child, failure, and output budgets.
- A read is best effort. An overlay may reduce user-driven mutation, but it is not a tree lock, immutable snapshot, native transaction, or lifetime owner.
- Continuation is Agent-directed. The implementation does not use fingerprints, similarity matching, hidden retry, or automatic re-anchoring to pretend a live tree is stable.
- Token counts are approximate because model tokenizers differ. A projection-specific estimator guides Plan; exact character or serialized-byte fences remain valid transport protections.
- The durable model-facing result is a `PromptNode`. XML uses native `PromptElement`; JSON, TOON, or another projection may use bounded text and grouping nodes. Syntax belongs to the Renderer rather than platform traversal.
- Future query-host process isolation is a separate containment boundary. Current process-local APIs do not introduce speculative handles or RPC-shaped abstractions.

## 7. Non-Goals

- Observing every descendant before planning.
- Keeping a target usable after every real owner and retained turn releases it.
- Silently reconstructing an unavailable element and reattaching an old Agent ID.
- Inferring application-specific concepts such as a feed card, IDE panel, or article.
- Guaranteeing exhaustive `Find` results over an unbounded live tree.
- Letting a `Composite` masquerade as an actionable accessibility element.
- Making `VisualElement` itself an independently disposable ownership token.
- Designing the process-local object model around hypothetical future RPC handles.
- Retaining a permanent compatibility layer around `VisualContextBuilder` or legacy `Everywhere.Interop.IVisualElement`.

## 8. Namespace and Assembly Boundaries

Platform-neutral contracts and implementation live in `Everywhere.Automation`. Platform implementations live in `Everywhere.Windows.Automation`, `Everywhere.Mac.Automation`, and corresponding future assemblies. Raw ABI, COM, P/Invoke, AX, AT-SPI, and native-handle helpers remain in platform Interop namespaces. `Everywhere.Prompting` owns reusable `PromptNode` construction and rendering.

The intended dependency direction is:

```text
Everywhere.Core --------------------+--> Everywhere.Automation --> Everywhere.Prompting
Everywhere.Windows -----------------|
Everywhere.Mac ---------------------|
Everywhere.Linux -------------------|
Automation tests -------------------+
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
