# VisualQuery Agent Contract

Implementation ownership and entry points follow [10-QueryAndScanImages.md](10-QueryAndScanImages.md): VisualQuery is Context-bound; structural operations await optional scan capture preparation; ReadText replaces the standalone VisualTextQuery entry point. The caller owns conversation turns.

## 1. Purpose

`get_visual_tree` is replaced by one bounded query contract because the result may be a disconnected forest, can contain logical Composite elements, and cannot promise exhaustive traversal of a live application.

The implemented tool name is `query_visual`. Its kernel request contains:

- one published integer visual element ID;
- traversal directions appropriate to the requested neighborhood;
- a 1-based offset for bounded structural member continuation when the element exposes `observedMembers`;
- a clamped result limit.

The current defaults are `directions=all`, `offset=1`, and `limit=128`; limits above 256 are clamped. An element without `observedMembers` has one query anchor and therefore accepts only offset 1. Otherwise offset pages its retained observed members, not an assumed immutable live-child collection. Tree continuation follows a returned child ID with another query.

The Agent does not select different operations for platform-backed and projected elements. Every published ID addresses a visual element through the same structural query; the implementation dispatches internally according to its real target kind.

The exact serialized request DTO remains a tool-integration decision. A later narrow selector is justified only by a demonstrated retrieval need; it must not expose the internal target branch under another name.

UI mutation is not part of VisualQuery. Invoke, pointer input, set-text, shortcuts, and related Computer Use behavior form a separate future contract.

## 2. Tool Description Requirements

An Agent reading only the tool description must understand that:

- the visual tree can be very large and changes frequently;
- every result is bounded and may be incomplete;
- every integer ID addresses one visual element through the same tool operation;
- `observedMembers`, when present, indicates that structural offset can page retained members;
- status describes only relevant unexpected conditions such as timeouts, limits, unavailable data, or provider degradation;
- absence of status means no known problem was observed, not that the result is exhaustive or immutable;
- continuation is best effort and another call may observe different state;
- an unavailable target is not silently reconstructed;
- retry is an Agent decision made by starting another call;
- a narrow follow-up query is often safer and more useful than requesting a broader tree.

Every visual query returns final text. The Builder uses PromptCompactElement internally for syntax and escaping, but completes rendering and target publication before returning. See [final-text allocation](09-FinalTextAllocation.md).

## 3. Compact Result Syntax

The visual result uses a small XML-like protocol optimized for model retrieval:

```text
<TextEdit id=7 name="Draft message" focused disabled/>
<Composite id=12 observedMembers=18 moreText>bounded preview</Composite>
```

The syntax is deliberately not valid XML:

- the tag name is the visual element type, including `Composite` for a projected aggregation;
- `id` is the published target ID used by later queries;
- safe nonempty attribute values without whitespace, control characters, or markup delimiters omit quotes;
- values containing `&`, `<`, `>`, `=`, quotes, apostrophes, backticks, or whitespace remain quoted and are escaped where necessary;
- attribute values and child text are escaped by the Prompting renderer;
- sparse Boolean facts are valueless flags;
- empty targets use self-closing form.

Only retrieval-relevant information is emitted. Normal `query_visual` output does not include a `complete` field, speculative action capabilities, implementation priority, PID, native handles, or a full state bitmask. The dedicated `list_windows` discovery result may include PID and process name, but still exposes only the published target ID as an address. Salient non-default states may appear as flags such as `focused`, `disabled`, `selected`, `readOnly`, `password`, or `offscreen`.

`status` remains an attribute because it contains bounded diagnostic information rather than a Boolean fact. `observedMembers` states how many source parts were retained by an aggregate projection; it is not a count of every current descendant. `moreText` means that additional text may exist, not that a specific next call is guaranteed to recover all of it.

The tool description must explain this grammar directly. It must not tell the Agent to parse the result as strict XML.

## 4. Target Resolution

Agent target IDs all address visual elements through the current Chat's `VisualContext`. The implementation then resolves its private target representation:

```text
Visual element ID
    -> ElementTarget
       -> retain/promote into current Agent turn
       -> bounded platform observation
    or CompositeTarget
       -> retain/promote into current Agent turn
       -> bounded projection of observed ordered parts
```

The branch is internal. Agent tools do not expose an Element-versus-Composite union. A Composite never becomes a fake platform `VisualElement`, and an Element is not wrapped as a one-member Composite merely to unify implementation code.

Root acquisition from focus, pointer, coordinates, native windows, or another explicit locator remains an internal Backend responsibility. Automatic attachments and `list_windows` publish the resulting Elements as Agent IDs before any Agent-facing query, capture, or action call. `list_windows` commits only window targets whose compact nodes survive its local prompt budget. Raw native handles never cross the Agent tool boundary, and a successfully returned target is published only when its compact target node survives final prompt rendering.

## 5. Query Behavior

For an Element target, the handler may:

1. query bounded scalar fields;
2. enumerate only the requested relations under traversal limits;
3. construct a bounded Snapshot;
4. normalize and project the partial observation;
5. atomically publish exactly the Element and Composite targets visible in the completed result.

For a Composite target, the handler starts from its retained ordered parts and returns a bounded slice or a new bounded projection. It does not pretend that the Composite is a platform Document or that its retained parts describe the complete current subtree.

Element scalar query, relation enumeration, image Snapshot, and actions remain receiver-centered platform operations. High-level orchestration belongs to the VisualQuery handler, Snapshotter, merged prompt builder, and any optional UI/input guard.

## 6. Structural Continuation

Normal retained-member paging emits `next` on the `visual-context` root, never a continuation sentence in `status`. Pass that 1-based offset back with the same target ID. The root attribute participates in the builder's local budget validation before target publication. It advances over the selected anchors; if observation stops before completing that range, status reports the incomplete observation and callers may overlap or retry it.

`query_visual` continuation applies only to structure. It follows the useful parts of directory browsing without claiming an immutable cursor:

- offsets are 1-based logical units;
- limits are clamped to safe bounds;
- every returned unit has its own hard content and serialization bound;
- a next offset, when emitted, is based on units actually returned;
- overlapping offsets are allowed so the Agent can reassess a changing boundary;
- an unavailable target fails explicitly;
- relation failure preserves safely returned items and status;
- failure never becomes a definitive `hasMore=false`;
- no fingerprint matching or automatic re-anchoring occurs.

Long scalar content is intentionally not overloaded onto this offset.

## 7. Text Continuation

`read_visual_text(target, offset, limit)` is the content counterpart to the structural query. It accepts a retained integer visual element ID and a zero-based UTF-16 offset, then returns one final compact text page:

```text
<visual-text target=7 offset=0 next=4096>bounded content</visual-text>
```

The returned `next` value is the next numeric offset and may be supplied unchanged as the following call's `offset`. Offsets count UTF-16 code units, matching ordinary .NET string indexing; they deliberately do not claim grapheme, scalar-value, word, or model-token semantics. Generated page boundaries do not split a surrogate pair, so a page may exceed `limit` by one UTF-16 code unit. Callers may request overlapping offsets, and an arbitrary caller-supplied offset is not normalized to a linguistic boundary.

`moreText` and `next` describe different observations. Structural `moreText` says that the earlier bounded preview did not exhaust its source and invites a content query. `next` says that the current text read observed another page and is the authoritative continuation for that call. Expected continuation is not a `status`. Text-query status is page-local and reports only failures or safety limits observed by that read; it does not replay status from an earlier structural observation.

For an Element, Windows prefers TextPattern and requests only the prefix required to cover `offset + limit` plus a small boundary probe; ValuePattern falls back to one timeout-bounded complete string read. Both paths then use the same local UTF-16 slicing rule. For a Composite, the reader exposes retained nonempty member text as one stream separated by `Environment.NewLine`. It walks members from the beginning but materializes only the prefix required for the requested page. A member that has no native text capability may use its complete retained observation; incomplete observed prefixes are never presented as complete fallback content.

This first implementation intentionally favors a small, inspectable, stateless contract over asymptotically optimal deep paging. Reading successive pages from the beginning can repeat work and may approach quadratic total work for a long document, while ValuePattern may still return the complete provider value. A coarse maximum accepted offset prevents an accidental unbounded replay request. Real native calls remain protected by their platform timeout, and the Snapshotter's first-page read remains independently bounded.

The tool uses character limits as a conservative transport bound, not as a model-specific tokenizer promise. A page is emitted atomically. If its fully escaped representation does not fit the local prompt budget, the result asks the Agent to retry the same offset with a smaller limit and does not expose the page-end offset. A provider failure from the current read is expressed through `status`; no retry is hidden inside the tool.

### 7.1 Alternative: Native Range Pagination

A more elaborate Windows implementation was designed and prototyped before the numeric-offset contract was selected. It remains a valid future optimization when evidence shows that repeated prefix reads dominate real workloads:

1. Preserve a provider-native position expressed in UIA `TextUnit.Character` units rather than deriving it from returned `string.Length`.
2. Obtain a fresh `DocumentRange` for each call and replay the saved start position with `MoveEndpointByUnit`.
3. Clone the remaining range, collapse its end to the page start with `MoveEndpointByRange`, and extend the end by a candidate number of native units.
4. Read the candidate range with `GetText(maxLength + 1)` and binary-search for the largest complete native-unit range that fits the UTF-16 transport limit.
5. Advance continuation by the number of units actually moved, then probe one more unit to distinguish an exact page boundary from the end of the document.
6. For a Composite, retain both the member index and that member's provider-native continuation so later pages can resume without replaying earlier members.

This algorithm must not equate UIA `TextUnit.Character` with one UTF-16 code unit. UIA defines it as a provider-controlled linguistic unit, and a provider may substitute a larger supported unit. Consequently, one native unit can exceed the requested transport page and make lossless bounded advancement impossible without another fallback. Live mutation can also change the meaning of a replayed native-unit count.

Do not restore this path solely because it is theoretically more efficient. First benchmark Win32, Avalonia, Chromium/Electron-like, multiline edit, rich-document, Unicode, mutation, and timeout cases. Reintroduction should preferably preserve the public numeric-offset contract by using internal checkpoints or another measured mapping strategy; an opaque public continuation is justified only if numeric positions demonstrably cannot provide acceptable behavior. Relevant UIA contracts are [`GetText`](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-gettext), [`MoveEndpointByUnit`](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-moveendpointbyunit), and [UI Automation Text Units](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-uiautomationtextunits).

## 8. Mutation and Retry Semantics

VisualQuery does not promise cross-call idempotence. Its guarantees are narrower:

- a committed target ID is never silently retargeted;
- each call has explicit request, traversal, and output bounds;
- unavailable targets fail rather than transparently re-anchor;
- the Agent decides whether to continue, overlap, query again, wait, use another perception source, or abandon the target;
- a new query is the explicit retry boundary;
- the implementation does not retry a timed-out operation inside the same call;
- an input guard may reduce user-driven changes but cannot prevent timers, network updates, animation, or provider reconstruction.

These semantics intentionally resemble file and terminal tools: the underlying source may change between reads, while bounded results, stable retained identity, explicit status, and caller-directed continuation remain useful.

## 9. Status as Agent Control Information

Status belongs to the operation that observed it and is not retained as target state. A later structural query or text read reports only its own current status. Projection-only budget omissions remain in the prompt where they occurred. Composite members retain the bounded scalar snapshot required for text fallback, not historical node status.

Compact status is emitted only when it should influence what the Agent does next. It may lead the Agent to:

- narrow traversal directions;
- query a specific child or target;
- continue from a smaller range;
- wait and explicitly query again;
- use a screenshot or another perception source;
- avoid an unresponsive application or PID;
- report that requested information could not be observed safely.

VisualQuery performs no delayed retry, retry queue, background recovery query, or hidden provider re-entry. A future query-host restart is a separate outer containment event and does not cause an old tool call to report fabricated success.

## 10. Search and Computer Use

Broad `Find` semantics are not part of the initial structural query merely because they are easy to add to an operation enum. If introduced, search remains bounded and must never claim exhaustive absence from an unbounded live tree.

Computer Use actions share target lookup but use a separate contract. The compact prompt does not advertise a capability list: element type gives the Agent a useful prior, while the actual action may still be unsupported or fail because provider behavior is application-defined. Such a call returns a capability-based failure at action time. Internally projected targets, including `CompositeTarget`, are rejected before platform code without requiring a separate Agent-facing target category.

## 11. Deterministic Agent Projection

For equivalent Snapshot inputs and initial Context state:

- target ordering is stable;
- tag, attribute, and flag ordering is stable;
- Composite member ordering is stable;
- continuation metadata is stable;
- retained IDs are reused consistently;
- new IDs follow final render order;
- status coalescing is deterministic.

This is deterministic projection, not deterministic observation of a mutable application.
