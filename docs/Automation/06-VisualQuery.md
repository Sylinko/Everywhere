# VisualQuery Agent Contract

Implementation ownership and entry points follow [10-QueryAndScanImages.md](10-QueryAndScanImages.md): VisualQuery is Context-bound; structural operations await optional scan capture preparation; ReadText replaces the standalone VisualTextQuery entry point. The caller owns conversation turns.

## 1. Purpose

`get_visual_tree` is replaced by one bounded query contract because the result may be a disconnected forest, can contain logical Composite elements, and cannot promise exhaustive traversal of a live application.

The implemented tool name is `query_visual`. Its kernel request contains:

- one published integer visual element ID;
- traversal directions appropriate to the requested neighborhood;
- a zero-based offset into each requested initial relation;
- a clamped result limit;
- whether represented elements should include bounding boxes.

The current defaults are `directions=all`, `offset=0`, `limit=128`, and `boxes=false`; limits above 256 are clamped. Offset zero begins with the first child or the immediately adjacent sibling. A positive offset skips that many results before the first returned relation item. When several directions are requested, the same offset is applied independently to each relation originating at the target. Relations recursively opened from returned nodes begin at zero. Tree continuation follows returned IDs and issues another query with the relation and offset needed by that call.

Offset is a logical request, not a portable implementation ceiling. macOS can seek into indexed AX child collections directly. Windows UI Automation tree walking is sequential and may stop at a platform work limit before reaching a large offset. Such a boundary is returned as a typed `LimitReached` failure with Agent-facing detail; it is not advertised as a universal tool argument restriction.

The Agent does not select different operations for platform-backed and projected elements. Every published ID addresses a visual element through the same structural query; the implementation dispatches internally according to its real target kind.

The exact serialized request DTO remains a tool-integration decision. A later narrow selector is justified only by a demonstrated retrieval need; it must not expose the internal target branch under another name.

UI mutation is not part of VisualQuery. Invoke, pointer input, set-text, shortcuts, and related Computer Use behavior form a separate future contract.

## 2. Tool Description Requirements

An Agent reading only the tool description must understand that:

- the visual tree can be very large and changes frequently;
- every result is bounded and may be incomplete;
- every integer ID addresses one visual element through the same tool operation;
- offset is zero-based and applies to each requested initial relation;
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
<Composite id=12 textLength=800/≥1048576>bounded preview</Composite>
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

When `boxes=true`, every represented Element or Composite with observed bounds includes `box=x,y,width,height`. A Composite box is the union of its observed member boxes and can include empty space between members; it is descriptive coverage rather than a promised clickable region. Missing provider bounds are omitted instead of becoming a zero rectangle. When `boxes=false`, structural query output omits all box attributes. Values use the platform desktop coordinate space: desktop pixels on Windows and X11, and desktop points on macOS. Multi-display origins may be negative. Box attributes are present during final token allocation, so their cost participates in the same result budget and target-publication decision as other attributes.

Other model-facing projections, including visual attachments, omit boxes by default. Diagnostic visual-tree export enables them explicitly. `query_visual` follows its `boxes` argument and does not affect Snapshot acquisition, scan-image placement, capture bounds, or native traversal.

`status` remains an attribute because it contains bounded diagnostic information rather than a Boolean fact. An incomplete body uses one structured `textLength=shown/total` attribute. Both numbers describe UTF-16 code units:

- `textLength=800/12000` means the exact total is 12,000;
- `textLength=800/≥4098` means at least 4,098 UTF-16 code units are proven;

The numerator is always the decoded UTF-16 length of the emitted body. `textLength` is omitted when that body is complete. A Composite body is one continuous logical prefix: projection stops at the first incomplete member instead of appending later fragments after a gap.

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
       -> readable logical text; structural queries report unsupported
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

If a target does not expose the requested relation, the operation reports an ordinary unsupported result. The tool description does not teach internal target categories or list special prohibitions. Composite text remains available through `read_visual_text`; structural inspection does not expose its retained source members.

Element scalar query, relation enumeration, image Snapshot, and actions remain receiver-centered platform operations. High-level orchestration belongs to the VisualQuery handler, Snapshotter, merged prompt builder, and any optional UI/input guard.

Main carries one complete `VisualQueryRequest` through its visual service, remote Context, and RPC boundary. Target, platform-default-root, and pre-publication-anchor RPC envelopes nest that MessagePack contract beside their identity data and output token budget. Automation Host passes the request unchanged into `VisualQuery.BuildAsync` for ordinary and scan-stream queries. `BuildAsync` consumes traversal, offset, node limit, and requested result fields; `VisualContextPromptOptions` contains only local prompt-budget and compression policy. Validation and future structural-query fields therefore stay at one execution boundary without parallel wire fields or hand-written mappings.

## 6. Structural Offset

`query_visual` offset applies to live relations rather than to a retained Composite member list. It follows the useful parts of directory browsing without claiming an immutable cursor:

- offsets are zero-based relation positions;
- each requested initial direction receives the offset independently;
- recursively traversed relations restart at zero;
- limits bound admitted result nodes rather than defining a universal maximum offset;
- an indexed platform may seek directly, while a sequential platform may spend bounded work reaching the requested position;
- an unavailable target or a platform limit reached before the requested position is reported explicitly;
- relation failure preserves safely returned items and does not imply definitive exhaustion;
- overlapping offsets are allowed so the Agent can reassess a changing boundary;
- no fingerprint matching or automatic re-anchoring occurs.

Long scalar content is intentionally not overloaded onto this offset. Use `read_visual_text` and its returned `next` value for text continuation.

## 7. Text Continuation

`read_visual_text(target, offset, limit)` is the content counterpart to the structural query. It accepts a retained integer visual element ID and a UTF-16 offset, then returns one final compact text page:

```text
<visual-text target=7 offset=0 next=4096 total=≥10485760>bounded content</visual-text>
```

The returned `next` value is the next numeric offset and may be supplied unchanged as the following call's `offset`. It is emitted only for a successful advancing continuation; a failed read retains the requested `offset` for an explicit retry and does not emit `next=offset`. Nonnegative offsets address the stream from its start. A negative offset is resolved from the current end, a successful result returns that resolved nonnegative position, and the page remains in forward order. Offsets count UTF-16 code units, matching ordinary .NET string indexing; they deliberately do not claim grapheme, scalar-value, word, or model-token semantics. Generated page boundaries do not split a surrogate pair, so a page may exceed `limit` by one UTF-16 code unit.

`textLength` and `next` describe different observations. Structural `textLength` says how much of a body was emitted and characterizes the best available total. `next` says that the current text read observed another page and is the authoritative continuation for that call. The text page's `total` uses the same exact or `≥` lower-bound notation without a shown-length numerator. Expected continuation is not a `status`. Text-query status is page-local and reports only failures or safety limits observed by that read; it does not replay status from an earlier structural observation.

For an Element, Windows prefers TextPattern and issues one bounded `DocumentRange.GetText(maxLength)` call from the beginning. The adapter slices the returned BSTR through `ReadOnlySpan<char>` before freeing it, so only the final page becomes a managed string. A prefix shorter than the bound proves an exact UTF-16 total; a filled bound proves `≥bound`. ValuePattern remains a timeout-bounded complete-string fallback. The usable structural-preview prefix is at most 1,048,576 UTF-16 code units. One explicit text read uses at most 10,485,760 usable code units, corresponding to a 20 MiB UTF-16 payload before provider and object overhead. The native request reserves one additional code unit as lookahead so a filled prefix cannot silently split a surrogate pair.

For macOS, `AXNumberOfCharacters` provides the total and `AXStringForRange` reads the requested range directly; AXValue is the complete-string fallback. These paths use the same UTF-16 page contract without inheriting the Windows prefix probe.

For a Composite, the reader exposes current member content as one stream separated by `Environment.NewLine`. Projection selects Text for a member with a non-whitespace text preview and Name otherwise; the target retains that source choice rather than the projection-time Snapshot. Later paging reads the selected live field, so preview and pagination do not silently switch between text and accessibility name. Every call reads members serially under one shared 10,485,760-code-unit probe budget and derives the total from those reads. The reader stops at the first unreadable member, lower-bound member, or exhausted shared budget and never appends later content across that gap. Negative offsets retain only the bounded suffix needed for the requested page. Member suffix reads reserve one extra output code unit so moving their start backward over a surrogate pair does not omit the member's final character. Composite observation is not transactional; an earlier member may change while a later member is being read.

If a Windows prefix fills its bound, a positive read can still return any requested page contained in that prefix with `total=≥bound`. A negative read is necessarily relative to the end of the observed prefix rather than the unknown actual end; it returns that prefix-tail page with explicit status. There is no deeper Windows read beyond this capability boundary. A coarse maximum accepted absolute offset keeps every requested page inside the explicit-read probe limit. Real native calls remain protected by their platform timeout.

The tool uses character limits as a conservative transport bound, not as a model-specific tokenizer promise. A page is emitted atomically. If its fully escaped representation does not fit the local prompt budget, the result asks the Agent to retry the same offset with a smaller limit and does not expose the page-end offset. A provider failure from the current read is expressed through `status`; no retry is hidden inside the tool.

### 7.1 Why Windows Does Not Use TextUnit Movement

UIA `TextUnit.Character` is provider-controlled and a provider may promote it to a larger supported unit. Controlled WinForms, Avalonia, and Chromium probes also showed large `Move` calls to be much slower and less consistent than bounded `GetText`. The implementation therefore does not use `Move` to estimate UTF-16 length or to create public continuation positions. Reintroducing native-unit movement requires new evidence across providers and must preserve the public UTF-16 contract. The relevant native contract is [`GetText`](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-gettext).

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

Status belongs to the operation that observed it and is not retained as target state. A later structural query or text read reports only its own current status. Projection-only budget omissions remain in the prompt where they occurred. Composite targets retain their ordered live member elements; later text reads query those elements again rather than treating projection-time scalar snapshots as target state.

Compact status is emitted only when it should influence what the Agent does next. It may lead the Agent to:

- narrow traversal directions;
- query a specific child or target;
- continue from a smaller range;
- wait and explicitly query again;
- use a screenshot or another perception source;
- avoid an unresponsive application or PID;
- report that requested information could not be observed safely.

VisualQuery performs no delayed retry, retry queue, background recovery query, or hidden provider re-entry. Automation Host replacement is a separate outer containment event. It invalidates the old remote Context and requires a fresh observation; it never causes an interrupted tool call to report fabricated success.

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
