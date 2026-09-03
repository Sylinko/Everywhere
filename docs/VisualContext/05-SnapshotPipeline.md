# Visual Context Snapshot Pipeline

## 1. Pipeline Boundary

The replacement pipeline contains two implementation phases with two hard boundaries:

```text
live platform tree
       |
       v
    Snapshot ------- platform-access boundary
       |
       v
Project PromptNode -- pure in-memory normalization, compression, prompt construction,
       |              and publication validation
       |
       v
PromptNode + atomic VisualContext target publication
```

Snapshot safety is one boundary; PromptNode syntax and publication are the other. Projection still performs conceptual normalization, structural compression, Composite creation, and admission passes, but they are private implementation details of one `VisualContextPromptBuilder`. There is no public intermediate Plan tree whose schema can drift from the final PromptNode.

## 2. Phase 1: Snapshot

### 2.1 Responsibility

Snapshot contracts, limits, traversal directions, and the implementation entry point are owned by `Everywhere.Automation`. This keeps the complete live-observation boundary beside `VisualContext`, `VisualElement`, query/enumerator contracts, retention, and the returned Snapshot model. Core selects inputs and policies but does not implement platform-neutral traversal.

The implemented Snapshot entry point is:

```csharp
public sealed class VisualContextSnapshotter
{
    VisualContextSnapshot CreateSnapshot(
        VisualContext context,
        IReadOnlyList<VisualElement> coreElements,
        VisualContextSnapshotLimits? limits = null,
        VisualContextTraverseDirections allowedTraverseDirections = VisualContextTraverseDirections.All,
        CancellationToken cancellationToken = default);
}
```

Snapshot:

- is the only phase that reads live platform visual state, including accessibility providers and composed native topology;
- traverses the platform's composed Element graph and does not assume that one root or relation chain uses one concrete backend;
- creates one Snapshot `VisualElementRetention`, uses synchronous `VisualElement.Query` and `VisualElement.CreateEnumerator`, and transfers that ownership into the returned `VisualContextSnapshot`;
- accepts already acquired core Elements; acquisitions without an Element receiver occur through `IVisualElementBackend.Query` before Snapshot traversal begins;
- preserves the existing traversal order and records its metadata;
- returns a bounded, partial observation forest;
- records facts and status without choosing serialization syntax;
- checks cancellation and aggregate risk between direct platform operations, retaining every admitted Element before any transient Enumerator owner is released.

A backend transition is not a new Snapshot root or a new risk domain by itself. A Screen-to-UIA edge participates in the same traversal priority, deadline, operation accounting, identity deduplication, and status flow as any other relation. The identity map uses the Context-wide, backend-qualified Element identity. Element snapshot type and observed fields describe the logical Element; they are not inferred from a concrete CLR class name.

Snapshot does not collapse transparent containers, create Composites, allocate prompt budget, render text, or assign Agent integer IDs.

`VisualContextSnapshotLimits` contains monotonic native-observation safety limits, including elapsed time, platform operations, node and child counts, text characters, and provider failures. Model-specific token estimation, output admission, Composite formation, and serialization limits belong to prompt projection rather than this Automation-layer contract.

### 2.2 Preserved Weighted Traversal

The implementation lifts the tuned algorithm rather than redesigning relevance:

- core Elements remain the highest-priority seeds;
- existing direction weights remain unchanged;
- `TraverseDistance.Reset()` and `TraverseDistance.Step()` remain unchanged;
- current type weights remain unchanged unless separately reviewed;
- visited Elements remain deduplicated within one Snapshot, while repeated relation observations may still establish a missing edge or advance their owning Enumerator;
- every currently supported direction remains available to the structural query;
- effective queue priority and a monotonic dequeue ordinal are stored on snapshot nodes.

Plan reuses this order. It does not calculate a second global importance score that competes with Weighted BFS.

### 2.3 Convergence Invariant

Every Snapshot-loop iteration must do at least one of:

1. commit one previously unseen snapshot node;
2. advance one bounded `IVisualElementEnumerator`; or
3. permanently close one traversal branch.

All risk measures are monotonic. Snapshot is bounded by at least:

- aggregate elapsed time from Snapshot's monotonic clock;
- total snapshot node count;
- maximum observed children per parent;
- logical platform-operation count;
- maximum text characters per node;
- total snapshot text characters or bytes;
- per-provider or per-root failure count;
- optional depth or edge limits only where real providers justify them.

Caller cancellation remains `OperationCanceledException`. A provider timeout normally degrades or closes only the affected branch. Aggregate deadline returns the safely observed partial snapshot with status describing incompleteness.

All Enumerators are disposed on success, duplicate detection, limit exhaustion, timeout, cancellation, and unexpected failure. Snapshot owns every Element that must outlive those Enumerators.

### 2.4 One Element with Huge Text

Snapshot handles an enormous text element as follows:

1. query lightweight identity, type, state, and bounds before text;
2. read the provider's ordinary scalar Value under the native timeout, or use a ranged API for document-style content when available;
3. retain only a bounded preview;
4. preserve `VisualElementSnapshot.HasMoreText`, attach status, and later expose a bounded continuation capability when more content may exist;
5. avoid tokenizing or structurally expanding a complete Value when local slicing is sufficient;
6. retain a skeleton when the provider call times out or otherwise fails.

This is derived from platform API capability. Test scenarios exercise the behavior but do not redefine the platform tree to force a preferred answer.

### 2.5 One Parent with Huge Child Count

Snapshot handles a virtualized or extremely large collection as follows:

1. enumerate lazily, by index, or in bounded pages;
2. stop at `MaxChildrenPerNode` or an earlier risk boundary;
3. report that more children may exist;
4. retain a continuation descriptor where the provider and target semantics allow it;
5. avoid realizing off-screen or not-yet-materialized items merely to count them;
6. preserve focused previous/next sibling exploration around a core Element.

Unknown Enumerator `Count = -1` is normal and does not force eager counting.

### 2.6 Slow or Broken Provider

- enforce the platform transaction timeout for every dangerous call;
- preserve fields and children already observed safely;
- attach scalar failure to a safe element skeleton;
- attach relation failure to the retained parent and mark descendants incomplete;
- charge repeated failures to the current Snapshot risk and provider/PID suppression policy;
- stop further calls to an exhausted provider while leaving unrelated providers and roots eligible;
- attach provider-unresponsive status to the applicable root;
- attach a transition failure to the nearest retained Element when one backend cannot produce the next backend's Element;
- retain only bounded, content-free health diagnostics outside the snapshot result;
- perform no automatic retry.

## 3. Phase 2: Project and Build PromptNode

Prompt projection is a pure in-memory transformation. The implemented public boundary is deliberately one component:

```csharp
public sealed class VisualContextPromptBuilder
{
    public PromptNode Build(
        VisualContext context,
        VisualContextSnapshot snapshot,
        VisualContextPromptOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

The builder may use private projection nodes and bounded Composite candidates while normalizing the Snapshot. Those types are not a second public forest, not a persistence format, and not a contract that another builder must reinterpret.

### 3.1 Normalization Order

The builder's planning pass performs these operations in fixed order:

1. establish stable root and sibling ordering;
2. coalesce snapshot entries resolving to the same native top-level root;
3. preserve every core node as an anchor under its coalesced root;
4. compute `HasRenderableDescendant` independently from `ShouldRenderSelf`;
5. collapse transparent containers while preserving child order;
6. identify source ranges and subtrees whose expanded representation has excessive structural cost;
7. create Composite projections with bounded previews;
8. expose important or interactive descendants as independent targets;
9. add concrete non-platform metadata required by current callers;
10. packetize render work and allocate the approximate target budget.

Performing normalization during provider traversal would make the output depend on discovery timing and conflate platform risk with presentation policy.

### 3.2 Root Coalescing

Several core Elements may belong to one platform root. Prompt output represents that root once with ordered anchors. Root identity uses observed platform identity first, with process and native-root identity only as fallback.

Coalescing never merges genuinely disconnected roots merely because their properties look similar.

### 3.3 Transparent Containers

`HasRenderableDescendant` and `ShouldRenderSelf` express different facts. A nameless Panel with no own text, state, action, bounds requirement, or status responsibility normally has:

```text
HasRenderableDescendant = true
ShouldRenderSelf = false
```

Its visible children are promoted in relative order without serializing the Panel. One legacy `IsVisible` flag cannot represent both states correctly.

### 3.4 Composite Projection

Composite creation is driven by generic observed structure and estimated render cost, never application-specific recognition.

Candidates include:

- a stable sequence of passive content fragments;
- one container subtree with excessive expanded cost;
- a contiguous range in a large collection;
- a mixed region whose preview and important descendants are much cheaper than its full structure.

Measurable policy may use member count, estimated structural overhead, or content-to-structure ratio. Mandatory rules are:

- source members remain queryable through the Composite target;
- normalized logical order is preserved;
- core Elements are never silently absorbed without an exposed anchor;
- selected interactive descendants receive independent Element targets;
- Composite is never an actionable platform Element;
- previews are bounded and deterministic;
- one oversized member splits into bounded units rather than blocking the Composite;
- nesting is bounded;
- no unbounded concatenation occurs before content limits;
- source status that affects completeness is preserved or summarized.

Compression is therefore a queryable projection of a complex visual range, not merely adjacent-string concatenation.

### 3.5 Reusing Existing Weights

Weighted BFS determines which observed node is more relevant. The builder's planning pass adds projection and admission only:

- traversal priority and ordinal order skeletons and exposed descendants;
- the actual PromptNode schema and Prompt renderer supply projection-specific cost and pruning behavior;
- the builder admits bounded fragments and validates their rendered survival;
- the builder does not replace the tuned order with a global `priority / cost` formula.

Progressive allocation is:

1. retain core skeletons and required query anchors;
2. admit Element and Composite skeletons in existing relevance order;
3. admit bounded previews and content in that order;
4. admit secondary metadata while budget remains;
5. reserve control budget for status and continuation.

### 3.6 Approximate Budget

`targetTokenBudget` is a local projection target, not an exact cross-model guarantee:

- the same Prompt renderer used by the final PromptNode validates structural cost and survival;
- relevance order and bounded fragment sizes preserve useful relative admission behavior;
- a safety margin may absorb common tokenizer differences;
- final estimates may be recorded for telemetry;
- no API claims exact compliance with every model tokenizer.

Every render unit also has a hard character or serialized-byte bound. A huge label, Composite part, line, or attribute cannot consume unbounded transport size.

Names implying impossible precision, such as `ExactTargetTruncator` or `preciseLimit`, are not retained.

### 3.7 Multi-Root Scheduling

Most normalized plans contain one actual root. A work-conserving hierarchical scheduler protects genuine multi-root cases without special-casing the common case:

- the outer scheduler uses Deficit Round Robin across roots;
- each root retains its Weighted BFS inner order;
- root quantum controls cross-root fairness rather than node relevance;
- when only one root is active, the algorithm naturally reduces to its exact inner order;
- work is packetized into bounded skeleton, preview, content, metadata, and status fragments;
- virtual rounds advance directly when no current head fragment is affordable.

This design avoids catastrophic starvation in extreme cases while introducing no separate common-case algorithm.

### 3.8 Status and Expansion

Prompt projection preserves Snapshot status and may add bounded status for projection or prompt-budget limits. A non-platform omission remains visible even when no exception occurred.

Expandable missing content resolves through an Element or Composite target. Raw platform IDs never appear as pretend Agent IDs. Status tells the Agent that information may exist; target kind and continuation tell it how to request more.

## 4. Projection Output and Publication

The same builder that normalizes and admits the Snapshot owns final syntax and publication. It returns structured content from `Everywhere.Prompting.Documents`; serialization into model text remains the Prompting layer's responsibility. These boundaries are mandatory:

- the model-facing result remains a `PromptNode`;
- the exact targets represented by that result are committed atomically into the existing `VisualContext`;
- the builder neither returns nor replaces the complete Context or target index.

### 4.1 Builder Responsibilities

1. reuse committed IDs for retained targets;
2. request monotonic new IDs only after normalization, Composite creation, and coarse admission;
3. build a bounded draft with provisional IDs and validate it through the actual Prompt renderer;
4. use one stable semantic schema for Element and Composite targets;
5. expose observed member count, independently exposed children, bounded preview, exceptional status, and continuation;
6. use Prompting primitives for invariant formatting and escaping;
7. remove renderer-omitted targets monotonically, add bounded budget status, and rebuild until the target set is stable;
8. commit the PromptNode's exact final publication batch before exposing the result;
9. abandon every superseded provisional batch without consuming IDs, and fail explicitly if required anchors cannot fit.

### 4.2 Native PromptNode Result

The native result is a tree of `PromptCompactElement` nodes. It renders familiar XML-like tags, compact scalar attributes, and valueless Boolean flags, but is deliberately not valid XML. A typical target is `<TextEdit id=7 name="Draft message" focused disabled/>`.

Element type is the tag name. `id`, bounded `name`, optional bounds, exceptional `status`, and Composite `observedMembers` are attributes. Safe nonempty values without whitespace, control characters, or markup delimiters omit quotes; all other values remain quoted and escaped. Sparse non-default facts such as `focused`, `disabled`, `selected`, `readOnly`, `password`, `offscreen`, and `moreText` are bare flags. The builder does not emit speculative action capabilities, implementation priority, or a normal `complete` field. Absence of `status` means that no relevant problem was observed; it does not claim an exhaustive or immutable read. `observedMembers` is the number retained in the Composite projection, not a count of every live descendant.

The builder does not concatenate markup with `StringBuilder` and wrap it as one `PromptText`. Untrusted attribute values and child text stay inside Prompting nodes so the renderer owns escaping.

`PromptCompactElement` treats its validated name, attributes, and flags as structural content. An element with no surviving child renders in self-closing form. Attribute-only targets therefore require no zero-width character, whitespace child, comment, or placeholder.

Self-closing support does not solve admission by itself. Target-bearing skeletons remain required PromptNodes or are admitted before construction. If required skeletons cannot fit, Build fails explicitly and abandons publication.

Other textual projections may use `PromptText`, `PromptTextChunk`, `PromptGroup`, and `PromptChunk`. Textual fragments remain syntactically valid under their declared pruning behavior. `PromptElement` remains available where valid XML is actually required; the compact visual protocol does not change its contract.

### 4.3 Pruning and Publication

The builder performs a bounded validation render before publication. It inspects the renderer's included, omitted, and truncated nodes, then monotonically shrinks the admitted target set until the returned locally limited PromptNode deterministically renders the same targets. Later `PromptDocument` pruning may shorten optional preview and secondary metadata. It must never:

- remove a published target skeleton;
- corrupt continuation;
- leave `VisualContext` pointing at a target the Agent never saw;
- make a textual structured fragment invalid;
- create a second Visual Context omission protocol.

`PromptRenderResult.OmittedNodes` remains Prompting renderer metadata. Known missing visual information is expressed as status before prompt construction.

The Prompt renderer accounts for container punctuation, nesting, escaping, Composite metadata, attributes, and status. The visual pipeline does not maintain a duplicate estimator schema or assume each node has a standalone fixed cost.

`VisualContextDetailLevel` controls semantic field inclusion and preview density. It does not select unrelated serialization formats.

### 4.4 Automatic Attachment Boundary

Automatic attachment processing supplies every valid attachment Element as a core of one bounded Snapshot. Snapshot limits therefore accumulate across the complete attachment forest instead of resetting for each process or native-window group. The prompt builder then publishes one coherent target set under the active visual target turn.

The first valid `VisualElementAttachment` carries the combined `PromptNode`; later attachments leave their content null and are reported as duplicates by chat-history projection. `VisualElementAttachment.Content` remains structured at MessagePack key 2. Its member formatter reads the legacy string form as `PromptText`, writes new values through the normal polymorphic `PromptNode` union, and leaves the surrounding attachment union and inheritance format unchanged. This is a forward migration boundary: current code reads both forms, while an older executable is not expected to understand a newly written PromptNode union at that key.

Flattening occurs only when `ChatHistoryBuilder` creates provider-facing `TextContent`. Snapshot disposal then releases every observed element that was not retained by final target publication; attachment disposal separately releases the original capture ownership.

## 5. Determinism

Determinism applies to equivalent snapshots, options, and initial publication state, not to a changing live UI.

- use a monotonic enqueue sequence to break equal traversal priorities;
- define input order for core Elements and roots;
- retain ordered children and Composite members;
- use sibling index followed by traversal ordinal as deterministic fallback;
- preserve relative child order through transparent-container collapse;
- coalesce roots before Composite creation;
- create Composites only after final logical ordering;
- emit known fields in fixed order and extension fields by stable key order;
- reuse retained IDs before allocating new IDs in final render order;
- do not depend on incidental `HashSet`, dictionary, or equal-priority queue enumeration.

## 6. Hot-Path Practices

- prefer `AsValueEnumerable()` in repeated bounded Snapshot and prompt projections when it avoids iterator and intermediate allocation;
- retain clear indexed loops or span operations when they are faster and easier to reason about;
- never expose lazy `IEnumerable` beyond the Enumerator or ownership lifetime on which it depends;
- materialize only bounded collections with explicit ownership;
- use a monotonic clock for elapsed risk;
- avoid logging in high-frequency traversal loops unless sampled;
- use invariant formatting for PromptCompactElement attributes and textual projections;
- avoid a second public full-forest DTO copy; private projection nodes should reference snapshot nodes and bounded Composite parts;
- dispose remaining priority-queue Enumerators when budget exhaustion ends traversal.
