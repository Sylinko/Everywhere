# VisualQuery Agent Contract

## 1. Purpose

`get_visual_tree` is replaced by a query contract because the result is bounded, may be a disconnected forest, can contain logical Composites, and cannot promise exhaustive traversal of a live application.

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

Use `operation`, not `action`, to distinguish retrieval from UI mutations such as invoke, click, set-text, shortcut, or pointer input.

## 2. Tool Description Requirements

An Agent reading only the tool description must understand that:

- the visual tree can be very large and changes frequently;
- every result is bounded and may be incomplete;
- one integer ID resolves either to one real Element target or one logical Composite target;
- Element and Composite capabilities differ;
- status explains timeouts, limits, unavailable data, degraded guards, and provider health relevant to the result;
- `offset`, `limit`, `nextOffset`, and `hasMore` support deliberate continuation;
- the same query may observe different state later;
- an unavailable target is not silently reconstructed;
- retry is an Agent decision made by starting another call;
- a narrow follow-up query is often safer and more useful than requesting a broader tree.

The output of every operation is a structured `PromptNode`. JSON, XML, TOON, or another rendered representation is a projection detail and does not alter tool semantics.

## 3. Target Resolution

Agent target IDs resolve through the current Chat's `VisualContext`:

```text
Element ID
    -> ElementTarget
    -> capability validation
    -> retain/promote into current Agent turn
    -> direct Element platform operation

Composite ID
    -> CompositeTarget
    -> Inspect / Expand / Find / bounded content projection
    -> never direct platform action
```

Root acquisition from a focused Element, point, or native window can begin without an existing Agent target. A successfully returned target is published only when represented in the result.

## 4. Inspect

`Inspect` performs the structural responsibility formerly assigned to `get_visual_tree`:

1. begin or use the current Agent turn;
2. resolve an Element or Composite from the current `VisualContext`, or acquire a root through the corresponding explicit platform Backend method using a retention from that Context;
3. create a bounded ownership batch and snapshot the requested neighborhood using the selected traversal directions and Weighted BFS;
4. normalize and project the partial observation;
5. build a PromptNode;
6. atomically publish exactly the Element and Composite targets visible in the completed result.

Element scalar query, relation enumeration, and image capture remain object-oriented operations. High-level orchestration belongs to the VisualQuery handler, Snapshotter, Planner, prompt builder, and optional UI/input guard.

## 5. Expand

`Expand` reads a bounded logical member slice from a Composite or another explicitly expandable target:

```text
query_visual(
    operation: expand,
    target: 42,
    offset: 1,
    limit: 20)
```

The response follows the conceptual `read_file` contract:

```text
VisualQuerySlice
|- Unit
|- Offset
|- Items
|- NextOffset
`- HasMore
```

Rules:

- Agent offsets are 1-based logical units;
- limits are clamped to safe bounds;
- every returned item has its own hard content and serialization bound;
- `NextOffset` is based on items actually returned;
- one huge member can be divided into several bounded units;
- overlapping offsets are allowed so the Agent can assess a changing boundary;
- no fingerprint matching or automatic re-anchoring occurs;
- an unavailable target fails explicitly;
- relation failure preserves returned items and status;
- failure never becomes a definitive `HasMore = false`;
- continuation describes best-effort progress, not a durable cursor into an immutable snapshot.

## 6. ReadContent

`ReadContent` applies only to a real Element or another target that exposes text content. Typical examples include Document, TextEdit, and long-text controls.

It does not reinterpret a Composite as a platform Document. Composite expansion uses its observed ordered parts and continuation semantics.

Platforms may support different ranged-text capabilities. Ordinary Value reads cover common scalar text, while document-style providers may expose ranged access; both remain under the provider timeout and Traverser aggregate budget. Continuation metadata must remain honest about whether the provider supplied a range or the adapter sliced a complete observed value.

## 7. Find

`Find` performs a bounded best-effort search from an Element, Composite, or current root. It may search already observed members first and perform additional bounded traversal when permitted.

An empty result does not prove absence from the complete application. Provider failure, traversal limit, content limit, or deadline status survives an otherwise empty result.

The implementation does not construct a global accessibility index or complete snapshot merely to provide exhaustive search semantics.

## 8. Mutation and Continuation

VisualQuery does not promise cross-call idempotence. Its guarantees are narrower:

- a committed target ID is never silently retargeted;
- each call has explicit request, traversal, and output bounds;
- an offset states where the Agent wants the next best-effort read to begin;
- unavailable targets fail rather than transparently re-anchor;
- the Agent decides whether to continue, overlap, inspect again, wait, use another perception source, or abandon the target;
- a new query is the explicit retry boundary;
- the implementation does not retry a timed-out operation inside the same call;
- the input guard reduces user-driven changes but cannot prevent timers, network updates, animation, or provider reconstruction.

These semantics intentionally resemble file and terminal tools: the underlying source may change between reads, while the tool remains useful through bounded results, stable target identity where retained, explicit status, and caller-directed continuation.

## 9. Status as Agent Control Information

Compact status is the primary signal for deciding what to do next. The Agent may use it to:

- narrow traversal directions;
- inspect a specific child or target;
- expand a Composite range;
- read a smaller content slice;
- wait and explicitly query again;
- use screenshot or another perception source;
- avoid an unresponsive application or PID;
- report that requested information could not be observed safely.

VisualQuery performs no delayed retry, retry queue, background recovery query, or hidden provider re-entry. A future query-host restart is a separate outer containment event and does not cause the old tool call to report fabricated success.

## 10. Action Semantics

Retrieval operations and UI mutations share target resolution but not the same operation enum.

Before any mutation:

1. resolve the published target through `VisualContext`;
2. promote a historical target into the active Agent turn when applicable;
3. validate its capabilities;
4. reject Composite before platform code;
5. optionally establish the user-visible overlay/input guard;
6. perform the Element-centered action directly under the platform timeout;
7. tear down any guard before returning.

An action changes the target UI. A later query may reuse retained Element identity when valid, but it begins a new observation and never claims the previous snapshot is current.

## 11. Deterministic Agent Projection

For equivalent snapshot inputs and initial Context state:

- target ordering is stable;
- field and attribute ordering is stable;
- Composite member ordering is stable;
- continuation metadata is stable;
- retained IDs are reused consistently;
- new IDs follow final render order;
- status coalescing is deterministic.

This is deterministic projection, not deterministic observation of a mutable application.
