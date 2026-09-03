# Visual Element and Target Model

## 1. Model Boundaries

| Representation | Meaning |
|---|---|
| `VisualElement` | One active canonical logical platform element in a `VisualContext`; it may wrap accessibility, display, or another native backend |
| `VisualElementQueryResult` | One bounded direct observation paired with the retained element that produced it |
| `VisualElementSnapshot` | Bounded scalar facts observed from an element during one query |
| `VisualContextSnapshotNode` | One node in a bounded partial observation forest |
| `ElementTarget` | An Agent-addressable retained element |
| `CompositeTarget` | An Agent-addressable logical grouping that may contain several elements and fragments |
| `PromptNode` | The renderer-independent final model-facing tree |

None of these types claims that the underlying application tree is immutable. Snapshot means a bounded set of facts already copied into managed state, not a provider lock.

## 2. Bounded Element Query

`VisualElement.Query` accepts a `VisualElementQueryRequest` describing only the scalar fields needed for one observation. It returns:

- the canonical element;
- copied scalar facts;
- an explicit `HasMoreText` fact when the provider or adapter observed content beyond the bounded preview;
- `AvailableFields`;
- `MissingFields`;
- an optional normalized failure.

`AvailableFields` and `MissingFields` are authoritative. A nullable property alone cannot distinguish an unavailable property, an unrequested property, and an observed `null` value.

`MaxTextCharacters` bounds the preview returned upstream. It does not assert that every provider can bound the native payload. Common UIA providers often expose only Value, so Windows may perform one timeout-bounded complete Value read and truncate locally. Document-capable providers may use ranged text APIs. `HasMoreText` prevents Snapshot from guessing whether a preview whose length equals the limit is complete. This is not a reason to reject Value or to fabricate an unsupported ranged operation.

Aggregate elapsed-time, operation, child, provider-failure, and output budgets belong to Snapshot traversal rather than being repeated in every query request. The platform timeout bounds each native RPC; Traverser bounds the series.

## 3. Receiver-Centered Operations

The live object exposes the operations that naturally belong to it:

```csharp
public abstract class VisualElement
{
    public string Id { get; }

    public virtual VisualElementQueryResult Query(VisualElementQueryRequest request);

    public virtual IVisualElementEnumerator CreateEnumerator(
        VisualElementRelation relation,
        VisualElementEnumerationOptions options);

    public virtual void Invoke();
    public virtual void SetText(string text);
    public virtual void Focus();
    public virtual void SendKeyGesture(KeyGesture keyGesture);
    public virtual string? GetSelectedText(int maxCharacters);
    public virtual Task<IVisualElementCapture> CaptureAsync(CancellationToken cancellationToken = default);
}
```

The base methods provide the common usability check and platform-exception conversion. Concrete elements implement query, relation, action, capture, and native release. The API does not round-trip through `VisualContext` merely to invoke behavior on an existing element.

`GetSelectedText` is an explicit bounded observation rather than a normal Snapshot field. It returns a textual representation of the element's current selection: platforms prefer true text ranges and may fall back to selected child labels for selection containers. Ordinary tree traversal must not query this transient state for every element. A null result means that no nonempty textual selection is available. Provider failures still cross the normal platform-exception boundary.

Query, navigation, and actions are synchronous because the underlying UIA/AX operations are synchronous RPCs. Capture may remain asynchronous where the graphics backend already has an asynchronous contract. A later query-host transport may expose asynchronous coarse-grained operations without changing this process-local object model.

## 4. Root Acquisition

Focused element, current pointer, explicit screen point, native window, a platform-default root, and similar acquisitions have no existing receiver. They therefore enter through the single query on the process-shared platform Backend:

```csharp
backend.Query(retention, VisualElementLocator.Focused);
backend.Query(retention, VisualElementLocator.Pointer, VisualElementResolution.TopLevel);
backend.Query(retention, VisualElementLocator.FromPoint(point), VisualElementResolution.Screen, request);
backend.Query(retention, VisualElementLocator.FromNativeWindow(handle), request: request);
```

The caller supplies the real ownership batch before acquisition. `retention.Context` is the only destination domain; no separate Context argument can disagree with it. A successfully acquired platform object is canonicalized and retained before it crosses the public boundary. The Backend must not retain the Context, retention, or result after returning. Failed root acquisition cannot fabricate an element merely to carry status; Snapshot attaches root-level status separately.

`VisualElementLocator` remains a serializable short-lived acquisition instruction for messages and deferred UI activation, not a generic execution facade or retained identity. `Focused` and `Pointer` sample mutable global state when replayed; `Point` preserves coordinates but not the content later occupying them; `NativeWindow` preserves a platform handle but remains subject to native lifetime and reuse. `VisualElementResolution` does not change locator stability. Subsequent observation, relation enumeration, capture, and actions are invoked on the resulting element.

Resolution is orthogonal to location: `Point + Direct` resolves the accessibility element at a point, while `Point + Screen` resolves the containing or nearest Screen without requiring a provider call. `Focused + Screen` resolves the Screen containing the focused element. The Backend may use native shortcuts, but only the final result enters the caller retention; intermediate native references or elements remain operation-local.

`Default` carries no spatial or object anchor. Resolution therefore selects the platform-default object at the requested level: Direct selects the platform-wide accessibility root, TopLevel selects the platform-default top-level window, and Screen selects the platform-default Screen. On Windows these are respectively the UI Automation desktop root, the first eligible top-level window in global Z-order, and the primary display. This remains orthogonal rather than overloading the Locator: `Default` consistently means “no anchor,” while the platform Backend owns the policy for each requested result level. A future macOS Backend is expected to map Direct to the AX system-wide object, but its TopLevel and Screen defaults must be validated against the native hierarchy before implementation.

## 5. Composed Platform Graph

The visual graph is platform-defined and may cross backends:

- Windows Screen nodes use Win32 monitor topology;
- Windows top-level and descendant nodes normally use UI Automation;
- a UIA top-level window may have a Screen parent;
- a Screen may enumerate UIA top-level windows as children;
- macOS may require distinct Screen, Application, TopLevel, and descendant implementations.

A relation result is required to remain in the same `VisualContext`, but it need not share its origin's concrete CLR type or native provider. Identity keys are backend-qualified so unrelated native identity domains cannot collide.

The exact macOS structural topology remains a native-validation checkpoint. An Application may contain multiple TopLevel windows, and Screen membership is not automatically the same relation as AX Parent/Child.

## 6. Relation Enumeration

`IVisualElementEnumerator` follows the familiar .NET Enumerator model while exposing bounded traversal metadata:

- `Current` is valid only after a successful `MoveNext` and before the end;
- `Index` is zero-based internally;
- `Count >= 0` means the total count is known without unbounded work;
- `Count == -1` means unknown;
- `HasMore` may use known count, provider metadata, or one-item lookahead;
- lookahead does not change `Current` or `Index`;
- provider failure is never converted into ordinary `HasMore == false`;
- `Reset` is supported only where the implementation can honestly reproduce its starting relation;
- disposal is idempotent.

Relation semantics are:

- `Parent` yields zero or one item;
- `Child` yields immediate children in platform-composed order;
- `PreviousSibling` and `NextSibling` begin at the adjacent sibling and continue outward;
- results remain lazy; a large child collection is not eagerly materialized merely to discover Count;
- callers clamp enumeration with `VisualElementEnumerationOptions` and the Traverser budget.

An Enumerator owns a `VisualElementRetention` for canonical elements it exposes. Disposing the Enumerator releases that batch. If a result must outlive the Enumerator, the caller retains it in the destination Snapshot, attachment, or turn before disposing the Enumerator. This is ordinary ownership transfer, not an execution pin.

## 7. Partial Success and Failure

Failure is data when useful partial observation exists and an exception when the requested operation cannot produce a meaningful result.

Normalized failure kinds distinguish at least:

- unsupported field, relation, or action;
- unavailable or stale element;
- native timeout;
- permission failure;
- provider or transport failure;
- platform failure whose exact category is unknown.

Windows converts `UIA_E_TIMEOUT` to `TimeoutException` while preserving the original `COMException` as its inner exception. Other provider exceptions retain their normalized HRESULT/message boundary. The implementation does not collapse all native failures into `null`, empty text, or enumeration completion.

A known element with incomplete scalar data remains a skeleton node with status. A relation or root failure that has no representable child contributes bounded status to the nearest retained parent or observation root.

## 8. Agent-Facing Targets

`VisualTarget` has two principal forms. The implemented shape is intentionally direct rather than an extensible ownership framework:

```csharp
public abstract class VisualTarget;

public sealed class ElementTarget : VisualTarget
{
    public required VisualElement Element { get; init; }
}

public sealed class CompositeTarget : VisualTarget
{
    public required IReadOnlyList<CompositePart> Parts { get; init; }
    public string? Preview { get; init; }
}
```

An `ElementTarget` retains the exact canonical element represented in the output. Its Agent-visible integer ID is a `VisualContext` publication ID, not UIA RuntimeId, HWND, object pointer, array position, traversal order, or native handle.

A `CompositeTarget` is a real logical target. It does not borrow the first source element's ID and is not actionable as though it were one native accessibility element. It can expose bounded member inspection, fragment expansion, and content paging according to `VisualQuery`.

## 9. Composite Semantics

Composite projection addresses a real output problem: providers often split one human-readable paragraph into many tiny elements, causing structural syntax to dominate useful content. Merging is therefore required, but it must preserve queryability and action honesty.

A Composite may contain:

- several source text fragments;
- several source elements;
- noninteractive structural members;
- interactive descendants retained as separate targets;
- explicit evidence that it contains multiple underlying members;
- continuation metadata when its full content or member list is not included.

Planning may merge adjacent fragments, collapse transparent containers, and group a large logical region without reading the platform again. Interactive controls are not silently flattened into text. Content paging resembles `ReadFileAsync`: bounded offset and limit, explicit `hasMore`, and no promise that a later live observation is identical.

The type name `Composite` intentionally describes structure rather than application semantics. It does not claim the region is a Document, chat message, article, card, or list unless the provider exposed that fact.

## 10. Status

`status` is the single bounded Agent-facing channel for omission and degradation. Omission is one status outcome rather than a separate parallel model.

Status may explain:

- requested fields missing or unsupported;
- native timeout or provider communication failure;
- child/text/result limit reached;
- traversal or output budget reached;
- live topology changed during observation;
- a provider/PID repeatedly failed and its remaining branch was skipped;
- input quiescence was unavailable;
- a projection was compressed and can be expanded.

Status strings are intentionally not decomposed into many fragile fields. Stable machine behavior uses the typed failure and bounded query contracts internally; the Agent receives concise context attached to the closest useful element, Composite, or root. Related messages may be deduplicated and coalesced before PromptNode construction.

## 11. Target Publication

Target IDs are published only after the merged prompt projection determines which targets are actually visible or queryable. Publication is provisional until commit:

1. `BeginTurn` establishes the current Agent ownership boundary.
2. `BeginPublication` captures the current monotonic ID and publication version.
3. `Add` reuses an already retained target ID or assigns a provisional new one.
4. Prompt construction renders a bounded validation copy, abandons targets removed by its local budget without consuming IDs, and rebuilds monotonically until target survival is stable.
5. `Commit` atomically adds every represented target to the active turn and advances the ID counter.

IDs are monotonically allocated and never reused within a Context. A historical target lookup during an active turn promotes that target into the active turn before returning it. Evicting an old turn does not make its integer IDs available again.

Equivalent observation and initial publication state must produce equivalent target ordering and allocation. Live-tree mutation can of course change the next observation.

## 12. Active Platform Identity

The identity guarantee is deliberately scoped to an active retained incarnation:

- every unknown native result first exposes a backend-qualified identity;
- an equal active identity reuses the existing canonical `VisualElement` even when the returned native pointer differs;
- the temporary native result is released after identity resolution;
- a miss creates one canonical element and retains it before exposure;
- the identity map does not independently retain the element;
- when the last ownership batch leaves, the exact entry is removed and the concrete native resource is released once;
- a later equal identity starts a new incarnation and is never attached to an old Agent ID.

On Windows, the RuntimeId is copied only on an identity-map miss. `IAlternateEqualityComparer<ReadOnlySpan<int>, UIAutomationRuntimeId>` permits allocation-free lookup against the cached SAFEARRAY. The retained UIA element owns one explicit COM reference independently of the client that returned it.

The system does not require stable raw COM pointers, RCW reuse, or process-lifetime RuntimeId uniqueness. It guarantees canonical managed identity only while the last retained representation of that RuntimeId remains alive, which is the strongest useful boundary supported by the actual application lifetime.

## 13. Lifetime and Release

Public `VisualElement` does not implement `IDisposable`. It is a shared canonical reference, so one caller cannot release it independently of the attachment, Snapshot, Enumerator, or Agent turn that owns it.

Deterministic release is still explicit:

1. a real owner disposes its `VisualElementRetention`;
2. the Context decrements each distinct identity entry once for that owner;
3. an entry still owned elsewhere remains active;
4. the final release removes the map entry;
5. the element calls its platform `ReleaseCore` exactly once.

Operation-local native owners use stack-friendly `using` values. Durable Windows COM wrappers dispose deterministically and may use finalization only as a leak fallback; finalization is not the normal element-lifetime policy. Borrowed handles such as `HMONITOR` have a no-op physical release but still follow the same logical identity lifetime.

Captured pixel buffers are independently disposable and do not retain the element implicitly. They expose physical pixel format, size, address, and stride; logical DPI is presentation metadata and is not part of the capture contract.
