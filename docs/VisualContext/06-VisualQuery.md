# VisualQuery Agent Contract

## 1. Purpose

`get_visual_tree` is replaced by one bounded query contract because the result may be a disconnected forest, can contain logical Composites, and cannot promise exhaustive traversal of a live application.

The implemented tool name is `query_visual`. Its kernel request contains:

- one published decimal Element/Composite target ID or one hexadecimal native-window handle;
- traversal directions appropriate to the requested neighborhood;
- a 1-based offset for bounded continuation;
- a clamped result limit.

The current defaults are `directions=all`, `offset=1`, and `limit=128`; limits above 256 are clamped. An Element has one anchor and therefore accepts only offset 1. A Composite offset pages its retained observed members, not an assumed immutable live-child collection. Element-tree continuation follows a returned child ID with another query.

The Agent does not select `Inspect` for an Element and `Expand` for a Composite. That distinction leaks an implementation detail and forces the caller to understand how compression happened. The same structural query resolves the target and dispatches internally according to its real target kind.

The exact serialized request DTO remains a tool-integration decision. A later narrow selector is justified only by a demonstrated retrieval need; it must not merely rename the Element-versus-Composite branch.

UI mutation is not part of VisualQuery. Invoke, pointer input, set-text, shortcuts, and related Computer Use behavior form a separate future contract.

## 2. Tool Description Requirements

An Agent reading only the tool description must understand that:

- the visual tree can be very large and changes frequently;
- every result is bounded and may be incomplete;
- one integer ID resolves either to one real Element target or one logical Composite target;
- querying either kind uses the same tool operation;
- status describes only relevant unexpected conditions such as timeouts, limits, unavailable data, or provider degradation;
- absence of status means no known problem was observed, not that the result is exhaustive or immutable;
- continuation is best effort and another call may observe different state;
- an unavailable target is not silently reconstructed;
- retry is an Agent decision made by starting another call;
- a narrow follow-up query is often safer and more useful than requesting a broader tree.

The output of every query is a structured `PromptNode` before rendering. The current model-facing projection uses `PromptCompactElement`, not a prebuilt XML string.

## 3. Compact Result Syntax

The visual result uses a small XML-like protocol optimized for model retrieval:

```text
<TextEdit id=7 name="Draft message" focused disabled/>
<Composite id=12 observedMembers=18 moreText>bounded preview</Composite>
```

The syntax is deliberately not valid XML:

- the tag name is the observed logical Element type, or `Composite`;
- `id` is the published target ID used by later queries;
- safe nonempty attribute values without whitespace, control characters, or markup delimiters omit quotes;
- values containing `&`, `<`, `>`, `=`, quotes, apostrophes, backticks, or whitespace remain quoted and are escaped where necessary;
- attribute values and child text are escaped by the Prompting renderer;
- sparse Boolean facts are valueless flags;
- empty targets use self-closing form.

Only retrieval-relevant information is emitted. Normal output does not include a `complete` field, speculative action capabilities, implementation priority, PID, HWND, or a full state bitmask. Salient non-default states may appear as flags such as `focused`, `disabled`, `selected`, `readOnly`, `password`, or `offscreen`.

`status` remains an attribute because it contains bounded diagnostic information rather than a Boolean fact. `observedMembers` states how many source parts were retained by a Composite projection; it is not a count of every current descendant. `moreText` means that additional text may exist, not that a specific next call is guaranteed to recover all of it.

The tool description must explain this grammar directly. It must not tell the Agent to parse the result as strict XML.

## 4. Target Resolution

Agent target IDs resolve through the current Chat's `VisualContext`:

```text
Element ID
    -> ElementTarget
    -> retain/promote into current Agent turn
    -> bounded platform observation

Composite ID
    -> CompositeTarget
    -> retain/promote into current Agent turn
    -> bounded projection of observed ordered parts
```

The branch is internal. A Composite never becomes a fake platform Element, and an Element is not wrapped as a one-member Composite merely to unify implementation code.

Root acquisition from focus, pointer, coordinates, or another explicit locator can begin outside the current tool and publish an Agent ID. The current tool also accepts a hexadecimal native-window handle as an explicit diagnostic/root bridge. A successfully returned target is published only when its compact target node survives final prompt rendering.

## 5. Query Behavior

For an Element target, the handler may:

1. query bounded scalar fields;
2. enumerate only the requested relations under traversal limits;
3. construct a bounded Snapshot;
4. normalize and project the partial observation;
5. atomically publish exactly the Element and Composite targets visible in the completed result.

For a Composite target, the handler starts from its retained ordered parts and returns a bounded slice or a new bounded projection. It does not pretend that the Composite is a platform Document or that its retained parts describe the complete current subtree.

Element scalar query, relation enumeration, image Snapshot, and actions remain receiver-centered platform operations. High-level orchestration belongs to the VisualQuery handler, Snapshotter, merged prompt builder, and any optional UI/input guard.

## 6. Continuation

Continuation follows the useful parts of the `read_file` model without claiming an immutable cursor:

- offsets are 1-based logical units;
- limits are clamped to safe bounds;
- every returned unit has its own hard content and serialization bound;
- a next offset, when emitted, is based on units actually returned;
- one huge member may be divided into several bounded units;
- overlapping offsets are allowed so the Agent can reassess a changing boundary;
- an unavailable target fails explicitly;
- relation failure preserves safely returned items and status;
- failure never becomes a definitive `hasMore=false`;
- no fingerprint matching or automatic re-anchoring occurs.

Ordinary Value reads cover common scalar text. A provider may return one timeout-bounded complete Value which the adapter slices locally, while a document-capable provider may support native ranges. Continuation must distinguish those facts rather than claim native random access everywhere.

## 7. Mutation and Retry Semantics

VisualQuery does not promise cross-call idempotence. Its guarantees are narrower:

- a committed target ID is never silently retargeted;
- each call has explicit request, traversal, and output bounds;
- unavailable targets fail rather than transparently re-anchor;
- the Agent decides whether to continue, overlap, query again, wait, use another perception source, or abandon the target;
- a new query is the explicit retry boundary;
- the implementation does not retry a timed-out operation inside the same call;
- an input guard may reduce user-driven changes but cannot prevent timers, network updates, animation, or provider reconstruction.

These semantics intentionally resemble file and terminal tools: the underlying source may change between reads, while bounded results, stable retained identity, explicit status, and caller-directed continuation remain useful.

## 8. Status as Agent Control Information

Compact status is emitted only when it should influence what the Agent does next. It may lead the Agent to:

- narrow traversal directions;
- query a specific child or target;
- continue from a smaller range;
- wait and explicitly query again;
- use a screenshot or another perception source;
- avoid an unresponsive application or PID;
- report that requested information could not be observed safely.

VisualQuery performs no delayed retry, retry queue, background recovery query, or hidden provider re-entry. A future query-host restart is a separate outer containment event and does not cause an old tool call to report fabricated success.

## 9. Search and Computer Use

Broad `Find` semantics are not part of the initial structural query merely because they are easy to add to an operation enum. If introduced, search remains bounded and must never claim exhaustive absence from an unbounded live tree.

Computer Use actions share target lookup but use a separate contract. The compact prompt does not advertise a capability list: Element type gives the Agent a useful prior, while the actual action may still be unsupported or fail because provider behavior is application-defined. Such a call returns the concrete failure at action time. Composite targets are rejected by target kind before platform code.

## 10. Deterministic Agent Projection

For equivalent Snapshot inputs and initial Context state:

- target ordering is stable;
- tag, attribute, and flag ordering is stable;
- Composite member ordering is stable;
- continuation metadata is stable;
- retained IDs are reused consistently;
- new IDs follow final render order;
- status coalescing is deterministic.

This is deterministic projection, not deterministic observation of a mutable application.
