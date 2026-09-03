# Visual Context Migration

## 1. Purpose

This chapter records the path from the legacy production implementation to the current target architecture. Stable ownership and behavior belong to the preceding numbered chapters; unresolved temporary deviations belong here or in repository-root `temp.md`.

The refactor is a clean internal replacement. Compatibility exists only long enough to migrate real callers and does not justify a permanent adapter framework.

## 2. Current-to-Target Mapping

| Legacy or removed concept | Current target responsibility |
|---|---|
| `ChatContext.VisualElements` / `ResilientCache<int, IVisualElement>` | one `ChatContext`-owned `VisualContext` with current/historical Agent turns |
| `VisualElementStore` | expanded into `VisualContext` |
| `VisualContextService` | removed; root acquisition and process-shared platform services are owned by `IVisualElementBackend` implementations |
| Windows `IVisualElementContext` / `VisualElementContext` | root acquisition moved to `WindowsVisualElementBackend`; interactive screen selection and text-selection monitoring are separate services |
| macOS/Linux `IVisualElementContext` / `VisualElementContext` | retained only as platform migration sources until native Context work |
| `WindowsVisualElementQuerySession` | removed; shared UIA services and root acquisition live in `WindowsVisualElementBackend`, while existing-element behavior lives in concrete elements |
| worker/Dispatcher/TaskScheduler/SynchronizationContext/Scope | removed after the native-timeout and real-call-path review |
| element Pin/Unpin/operation leases | replaced by real-owner `VisualElementRetention` batches |
| flat element LRU | current Agent turn plus oldest-first whole-turn history |
| legacy `Everywhere.Interop.IVisualElement` | new canonical `Everywhere.Automation.VisualElement` |
| `VisualElementSiblingAccessor` | `VisualElement.CreateEnumerator` |
| `VisualContextBuilder` | Snapshot plus merged PromptNode projection and publication |
| `BuiltVisualElements` | provisional/atomic `VisualContext` target publication |
| `get_visual_tree` | `query_visual` / `VisualQuery` |

## 3. Implemented Foundation

The replacement foundation currently includes:

- `Everywhere.Prompting`, including `PromptCompactElement` support for required attribute-only/self-closing compact nodes;
- the platform-neutral `Everywhere.Automation` assembly, including the Snapshot model, Snapshotter, monotonic Snapshot limits, and traversal directions;
- synchronous receiver-centered `VisualElement` query, enumeration, actions, and platform failure conversion;
- `IVisualElementBackend` as the non-retaining root-acquisition entry point and owner of process-shared platform services;
- one sealed platform-neutral `VisualContext` as the identity, ownership, publication, and Agent-turn domain;
- explicit `VisualElementRetention` ownership batches;
- active-incarnation identity maps with allocation-free alternate identity lookup;
- provisional publication that consumes no Agent ID until commit;
- current-turn ownership, historical target promotion, and automatic whole-turn/soft-target-capacity eviction;
- Windows UIA and Win32 Screen concrete elements;
- one Backend Query with orthogonal default/focused/pointer/point/native-window Locators and Direct/TopLevel/Screen Resolution;
- separate Windows interactive screen-selection and text-selection-monitor services;
- the unmanaged CsWin32 UIA facade and deterministic COM ownership model;
- one process-shared immutable-policy Windows UIA client and TreeWalker;
- `WM_DISPLAYCHANGE`-driven immutable Windows display topology;
- Invoke/Toggle/Select/Expand, ValuePattern SetText, Focus, SendKeyGesture, and clickable-point fallback;
- declarative scenario/seed infrastructure, Mock backend, and controlled WinForms/Avalonia/CefSharp TestApps;
- the `query_visual` tool over Element and Composite targets through the canonical Snapshot/PromptNode pipeline;
- retained Windows UIA behavior probes.

The worker, custom scheduler, SynchronizationContext, bounded-proxy, watchdog, Scope, Direct Scope, operation lease, pin, and per-Scope client implementation has been deleted. It did not provide a real termination boundary for a synchronous RPC and added lifetime complexity unrelated to the production call path.

Windows production observation, actions, attachments, debugger, text-selection, interactive picking, and Chat target lookup now use the canonical `VisualElement`, one neutral `VisualContext` per chat, and the singleton `WindowsVisualElementBackend`. Root acquisition uses one Backend Query with an independent Locator, Resolution, optional scalar request, and caller-created retention; application UI uses separate screen-selection and text-selection-monitor services. `ChatContext` constructs its Context directly, including after deserialization and for derived Agents. The Backend owns shared UIA services but never retains Contexts or Elements. Automatic attachments and the `query_visual` tool now share the replacement Snapshotter and merged PromptNode builder. The legacy `VisualContextBuilder` remains only in the debugger and characterization tests until those callers are intentionally migrated.

## 4. Migration Stages

### 4.1 Preserve Characterization Evidence

Retain characterization coverage for:

- Weighted BFS directions, distances, weights, and deduplication;
- equal-priority ordering;
- visibility propagation and root construction;
- Enumerator lifetime and partial failure;
- platform exception shapes;
- UIA pointer/RuntimeId/client behavior;
- representative application trees from declarative scenarios.

Characterization tests describe observed behavior. Target acceptance tests may intentionally replace it where the specification requires a different result.

Status: in progress and intentionally permanent for native probes.

### 4.2 Flatten Native Execution

Completed work:

1. remove mandatory asynchronous dispatch from single native calls;
2. remove workers, Channels, Runtime scheduler/context, watchdog, caller proxies, and health snapshots;
3. remove execution Scope and Direct Scope hierarchies;
4. make existing-element operations direct receiver methods;
5. retain native UIA/AX timeout as the per-call safety boundary;
6. retain Traverser aggregate elapsed/operation/failure limits as the sequence boundary;
7. reserve whole query-host process isolation as the stronger future containment boundary.

Do not reintroduce an in-process worker merely to move blocking elsewhere. A later worker is justified only by demonstrated platform thread affinity, useful parallelism, or a containment design with an honest external kill boundary.

### 4.3 Install Real Ownership and Identity

Completed work:

1. make each attachment, Enumerator, Snapshot, and Agent turn own an explicit retention batch;
2. make identity maps canonicalize without independently caching;
3. retain each canonical identity at most once per owner;
4. remove the map entry and release native state when the final owner leaves;
5. remove public element disposal and internal operation pins;
6. transfer ownership add-before-release between Enumerator, Snapshot, and publication;
7. allocate Agent IDs monotonically and never reuse them;
8. promote historical lookup into the current turn;
9. evict completed history by whole turn rather than arbitrary individual element;
10. automatically enforce the configured completed-turn count and soft retained-target count after every successful turn completion.

Automatic chat attachment processing now uses the replacement Snapshotter and merged PromptNode builder under the Context-owned current turn. The attachment stores structured prompt content at its existing MessagePack key and renders it only at the provider boundary; old flattened strings remain readable through a member-level compatibility formatter.

### 4.4 Complete Windows Platform Migration

Implemented:

- one shared `WindowsVisualElementBackend` owns UIA client and TreeWalker without retaining caller Contexts;
- each chat directly owns an independent platform-neutral `VisualContext` identity/target domain;
- UIA timeout policy is configured once before publication;
- unknown native elements include cached RuntimeId and canonicalize before exposure;
- duplicate native pointers with equal RuntimeId reuse one high-level element;
- known-element refresh omits RuntimeId and identity-map work;
- every CacheRequest and returned native element is operation-local;
- Screen and UIA relations compose through backend-qualified identity;
- concrete Windows elements propagate their immutable Context and Backend through relation results;
- Screen topology and capture avoid fake UIA/provider semantics;
- standard default invocation, MSAA-compatible LegacyIAccessible default action, SetText, Focus, shortcut delivery, and clickable-point fallback are migrated;
- bounded TextPattern, SelectionPattern, and LegacyIAccessible selection reads are migrated without making selected text a default Snapshot field;
- top-level HWND bounds preserve the proven DWM extended-frame correction with UIA fallback.

Remaining Windows vertical behavior slices:

1. user-visible overlay/input guard and virtual cursor;
2. broader Computer-Use operation design and provider-specific evidence.

The legacy Windows UIA and Screen element implementations and the production RCW dependency have been removed after their vertical behaviors and useful comments were migrated. The retained RCW dependency is test-only and supports explicit behavior probes.

### 4.5 Migrate Snapshot

The replacement Snapshotter is now the production path for automatic visual attachments and `query_visual`. The legacy Builder remains temporarily callable only by the debugger and characterization tests; it is no longer an Agent-facing boundary.

Implemented replacement Snapshot foundation:

1. own `VisualContextSnapshotter`, `VisualContextSnapshotLimits`, `VisualContextTraverseDirections`, and the returned Snapshot model in `Everywhere.Automation` rather than the Chat/Core layer;
2. create one Snapshot retention;
3. accept core Elements acquired through the platform Backend and use synchronous `VisualElement.Query` plus lazy `CreateEnumerator`;
4. retain every observed result before advancing or disposing the transient Enumerator;
5. preserve the existing direction/type weights, distance transitions, deterministic queue tie-breaking, and traversal metadata;
6. separate element materialization from relation observation so a repeated Parent still attaches another core sibling and a repeated relation Enumerator still advances;
7. enforce monotonic elapsed, operation, node, per-child-relation, per-node text, aggregate text, and provider-failure limits;
8. preserve bounded scalar and relation status, including explicit `HasMoreText` from platform adapters;
9. return the retention as part of disposable `VisualContextSnapshot`;
10. dispose all Enumerators on completion, cancellation, limits, provider failure, and unexpected exit.

Snapshot follow-up work:

1. add provider/PID-scoped suppression without starving unrelated roots;
2. model provider-supported ranged continuation beyond the current `HasMoreText` fact.

Snapshot must be useful with one extremely large text element and with one parent exposing hundreds of thousands of virtualized children. It must not first materialize either extreme.

### 4.6 Build Merged Prompt Projection

Implemented foundation:

- independent descendant/self visibility;
- transparent-container collapse;
- adjacent passive-label Composite projection with bounded previews;
- Snapshot and projection-budget status propagation;
- native `PromptCompactElement` projection with compact escaped attributes, sparse bare flags, and renderer-owned escaping;
- attribute-only/self-closing target skeletons;
- `CompositeTarget` membership retained by the active turn;
- renderer-validated, monotonic admission;
- provisional target IDs and atomic current-turn publication without a public intermediate Plan model;
- deterministic root-interleaved relevance admission that preserves the exact inner ordering when only one root exists;
- automatic attachment integration that produces one structured prompt for the complete bounded attachment forest.

Prompt-projection follow-up work:

- evidence-based root coalescing and full fragment-cost Deficit Round Robin scheduling beyond the current initial root-fair admission order;
- broader Composite candidates for expensive ranges and subtrees;
- provider-supported long Element content continuation beyond the current local bounded Value projection.

Before disposing Snapshot, publication retains every surviving `ElementTarget` in the active Agent turn. Elements observed but not published then release with Snapshot.

### 4.7 Implement VisualQuery

Implemented: one `query_visual` structural query handles both Element and Composite targets, returns structured PromptNode content, applies a default node limit of 128 and hard maximum of 256, and performs no hidden retry. Target resolution selects live Element traversal or a retained Composite-member slice internally. Offset is 1-based; Composite targets use it for retained observed-member paging, while Element targets accept only offset 1 and direct the Agent to a returned child ID for narrower continuation. Broader search and Computer Use actions remain separate contracts to design from real calling needs.

Each Agent call begins or participates in a `VisualTargetTurn`. Successful lookup of a historical target promotes it into the active turn. Completing the chat turn moves that ownership into history; retention policy later trims whole old turns.

### 4.8 Migrate Actions and Callers Together

Completed caller cutover includes action validation and invocation, automatic visual-context attachment, `query_visual`, statistics, and ChatContext target storage. Remaining contract migration includes:

- StrategyEngine sibling queries;
- debugger migration away from the legacy Builder;
- prompt/detail-setting cleanup after the legacy Builder is deleted.

A Composite must never be adapted to pretend it is a `VisualElement`. It is rejected before platform invocation.

### 4.9 macOS Implementation

Treat macOS as several concrete element domains from the beginning:

1. implement AX query, relations, actions, ownership, equality, failure, and timeout through native macOS validation;
2. migrate NSScreen as a separate element implementation;
3. keep Screen operations outside fake AX provider/PID semantics;
4. probe descendant/TopLevel/Application Parent chains and multi-window Applications;
5. stop for design review before publishing cross-backend Parent/Child/sibling topology;
6. validate coordinate conversion, permissions, capture, mutation, unresponsive providers, and native release on macOS;
7. preserve backend-independent Snapshot, VisualQuery, and Agent targets.

Only declarations and compile-time preparation may be completed from Windows. Native behavior is not inferred from sparse documentation or another platform.

### 4.10 Cut Over and Delete Legacy Production Code

After production-entry verification succeeds, remove:

- `get_visual_tree` and its prompt contract;
- `VisualElementSiblingAccessor`;
- old visual-tree DTOs;
- pre-render integer-ID assignment;
- obsolete merge logic;
- unused JSON/TOON/XML traversal branches;
- compatibility constructors and adapters without callers;
- legacy `Everywhere.Interop.IVisualElement` and the remaining macOS/Linux implementations after their final behavior review.

The legacy resilient element cache and old Windows UIA/Screen implementations are already removed from production.

### 4.11 Harden Experimental Computer-Use Actions

After the core query, target, and caller migration:

1. audit the standard UIA Pattern and AX action sets;
2. decide whether the Agent contract keeps one best-effort default action or exposes explicit select/toggle/expand/collapse/input intentions;
3. add bounded VirtualizedItem realization and ScrollItem visibility preparation with re-observation;
4. define available post-action evidence without claiming universal semantic success;
5. integrate overlay, input hooks, fixed-magic input, foreground policy, and virtual cursor;
6. characterize WinForms, Avalonia, browser/Electron-style, and representative real applications;
7. give declarative scenarios deterministic per-element callbacks and state transitions.

Provider quirks and input simulation remain in the high-level Windows action policy, not the native Interop ownership layer.

## 5. Cutover Invariants

The following concerns migrate together because splitting them would expose inconsistent target identity:

- `query_visual` description and result schema;
- ChatContext target lookup;
- PromptNode target publication;
- Element-versus-Composite action rejection;
- automatic context attachment;
- debugger rendering;
- status/continuation syntax;
- Element-versus-Composite statistics.

No target ID is published before its final target-bearing PromptNode is known to survive. No legacy adapter makes a Composite actionable. No ID silently refers to a reconstructed different element.

## 6. Implementation Practices

- Treat one `VisualContext` as a serialized domain; do not add locks for hypothetical concurrent mutation.
- Hold no synchronization primitive across platform work.
- Configure shared native client timeout policy once before publication.
- Keep CacheRequests and returned native wrappers operation-local.
- Dispose every Enumerator on every exit path.
- Retain an escaping element in its destination owner before disposing the source Enumerator/Snapshot.
- Do not let an identity map or Agent-ID table become an accidental element owner.
- Use `DisposeHelper.DisposeToDefault(ref field)` for nullable disposable fields in normal lifecycle code.
- Use `TaskExtensions.Detach` rather than `_ = DoAsync()`.
- Use `AsValueEnumerable()` where it improves repeated bounded transformations without obscuring clearer indexed or span code.
- Keep hook callbacks constant-time and allocation-free.
- Preserve useful legacy comments while migrating the behavior they explain.
- Record a temporary workaround or deviation immediately in `temp.md`.

## 7. Completion Criteria

The refactor is complete when:

- `ChatContext` owns `VisualContext` as its only Agent target domain;
- Backend owns only root acquisition and shared platform services and never retains caller Contexts or Elements;
- sealed platform-neutral Context owns its chat-specific identity, ownership, and Agent-target domain;
- attachments, Enumerators, Snapshots, and Agent turns express all element lifetime through real ownership batches;
- public Query Session, Scope, worker dispatch, operation pins, and legacy element accessors are gone;
- Snapshot performs all live platform reads through direct Backend root acquisition and receiver-centered Element operations;
- heterogeneous concrete elements own their behavior without leaking backend assumptions upward;
- merged PromptNode planning and construction are platform-free;
- final output is PromptNode plus atomic current-turn target publication;
- VisualQuery and actions resolve discriminated target types correctly;
- Windows production-entry tests pass;
- macOS behavior is validated natively;
- obsolete compatibility and render paths are deleted.
