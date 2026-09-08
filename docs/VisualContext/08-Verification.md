# Visual Context Verification

## 1. Strategy

Verification has five distinct evidence layers:

1. **pure contract tests** for Context identity, ownership, turns, publication, prompt projection, and PromptNode;
2. **deterministic Mock scenarios** for traversal, limits, mutation, failures, and exact attempts;
3. **controlled real TestApps** for provider trees and native actions across WinForms, Avalonia, and CefSharp;
4. **native real-web probes** for mutable public pages rendered by WebView2, WKWebView, or a supported Linux WebKit provider;
5. **explicit native probes** for platform behavior that public documentation does not fully specify.

No layer may claim evidence supplied only by another. Mock timeout injection verifies policy and status, not that UIA terminates a blocked provider. A controlled UI dispatcher hang verifies a real provider boundary, not arbitrary element-scoped failure. A native or real-web probe reports observed platform behavior and remains explicit rather than becoming a required deterministic CI assertion.

[Testing](Testing.md) defines the declarative scenario infrastructure. Scenario and seed reproduce one generated logical UI within the same commit. Later code changes are allowed to change the generated result.

## 2. Shared Backend Boundary

Verify:

- production dependency injection supplies one platform Backend singleton;
- creating and disposing multiple Contexts does not recreate or dispose that Backend;
- Backend root acquisition retains a returned element in the caller-provided retention before exposure;
- Windows initializes one UIA client and Content View TreeWalker with the fixed timeout policy;
- concurrent Backend initialization is serialized only around the known UIA activation constraint;
- direct element operations do not route through a worker, Dispatcher, TaskScheduler, SynchronizationContext, or Scope;
- Backend disposal releases shared platform objects exactly once;
- no test preserves a deleted execution layer through a forwarding compatibility type.

Platform timeout and traversal budget are tested separately. A single provider timeout must not be described as a complete Snapshot deadline. Snapshot must stop between calls when its aggregate budget is exhausted.

The platform-neutral Snapshotter is verified from `Everywhere.Automation.Tests` against deterministic Mock providers. Core-level tests begin at input selection, merged PromptNode projection, target publication, and Agent integration rather than re-owning the traversal implementation.

## 3. Context, Retention, and Identity

Verify:

- one Context accepts only its own live `VisualElementRetention` values;
- a retention owns each canonical element at most once even if it encounters it repeatedly;
- two retentions can own the same element independently;
- disposing one owner leaves the element usable through another;
- disposing the final owner removes the exact identity-map entry and calls platform release once;
- a later equal platform identity creates a new incarnation after the old final release;
- equal identities observed through different native pointers return the same managed element while retained;
- an identity map does not keep an otherwise unowned element alive;
- failed candidate creation or canonicalization releases the temporary native candidate;
- a Context cannot publish or query with a retention belonging to another Context;
- Context disposal releases active turn, historical turns, attachments/Snapshots, and identity entries without disposing Backend-owned native services;
- losing the last external reference to a ChatContext leaves no Backend/client/static reverse reference to its Context or Elements;
- completing a turn automatically applies the configured whole-turn and soft target-count retention limits.

Use a concrete mock element with an exact release counter. Do not add production release hooks only for tests; expose evidence through the Mock subclass or normal native wrapper behavior.

## 4. Agent Turn Lifetime

Verify:

- at most one current `VisualTargetTurn` exists;
- disposing an incomplete turn abandons every target and retention it acquired;
- completing a nonempty turn transfers it into history;
- completing an empty turn releases it immediately;
- lookup searches current turn first and history newest-to-oldest;
- successful historical lookup during an active turn promotes the target into that turn;
- trimming history evicts oldest complete turns rather than individual targets;
- an element referenced by several turns survives until the final owning turn is evicted;
- `TargetCount` counts distinct retained Agent IDs across overlapping turns;
- Agent IDs increase monotonically and are never reused after eviction.

A representative lifetime test should model three turns with overlapping sets, evict the first whole turn, and assert that only elements absent from later turns release.

## 5. Publication

Verify:

- an abandoned provisional batch consumes no target ID;
- committing a batch advances the counter only by newly represented targets;
- republishing a retained element reuses its committed ID;
- two distinct Composites do not alias merely because they contain the same first element;
- a stale batch cannot commit after Context publication state changes;
- every committed `ElementTarget` is retained by the active turn before Snapshot ownership is released;
- PromptNode construction failure leaves publication unchanged;
- a committed integer ID is never silently rebound to a later incarnation.

## 6. Enumerator Contracts

Verify every concrete and Mock Enumerator:

- `Current` validity follows .NET Enumerator rules;
- `Index` starts at `-1` and advances only after successful `MoveNext`;
- known `Count`, unknown `Count == -1`, and `HasMore` are consistent;
- lookahead does not change `Current` or `Index`;
- provider failure is never returned as ordinary `HasMore == false`;
- Parent yields zero or one item;
- Child and sibling order matches the accepted composed topology;
- limits do not cause eager collection materialization;
- every yielded canonical element remains owned until Enumerator disposal;
- an element retained into Snapshot remains alive after Enumerator disposal;
- disposal is idempotent and releases the Enumerator's ownership batch exactly once;
- every traversal exit path disposes active Enumerators.

## 7. Windows UI Automation Interop

### 7.1 Native Ownership

Verify:

- `UIAutomationElement` owns one operation-local returned reference;
- `Realize` performs one AddRef and creates an independently disposable `UIAutomationElementReference`;
- repeated Realize calls create independently balanced owners;
- known-element `Acquire` creates and releases one temporary AddRef;
- clients, TreeWalkers, CacheRequests, Patterns, BSTRs, SAFEARRAYs, VARIANTs, returned elements, and durable References balance on success and failure;
- `ComReference.Dispose` suppresses finalization and releases once;
- finalization is only a leak fallback and never required by normal tests;
- copied scalar data remains valid after native buffers are released.

### 7.2 Cache and Identity

Verify:

- unknown root and relation results include RuntimeId in the operation-local cache;
- alternate span lookup allocates no durable RuntimeId on an identity hit;
- an identity miss copies RuntimeId once and Realizes one durable Reference;
- a duplicate candidate releases its operation-local pointer without allocating a second high-level canonical element;
- known-element refresh omits RuntimeId and does not re-enter the identity map;
- every provider-backed Enumerator step creates and disposes its own CacheRequest;
- every `Cached*` Pattern-property read has an explicit matching CacheRequest property; adding the Pattern alone is not considered sufficient;
- Pattern-only operations do not accidentally transfer large scalar properties, especially ValuePattern.Value during `SetText`;
- cached Value is the common scalar text path and TextPattern is an optional ranged-document supplement;
- `UIA_E_TIMEOUT` becomes `TimeoutException` with the original exception retained;
- unsupported, unavailable, timeout, and unknown provider failure remain distinguishable.

### 7.3 Retained Native Probes

Keep explicit probes for:

- same-client and cross-client returned pointer/RuntimeId identity;
- retained element use across compatible MTA workers;
- which client/cache request supplies transaction timeout;
- connection-timeout versus transaction-timeout behavior;
- progress through one shared client while another provider call is blocked;
- native/managed allocation for repeated acquisition and Parent navigation;
- concurrent client/TreeWalker initialization behavior.

Probe output includes OS/build, architecture, .NET runtime, timing, HRESULT, pointer identity, RuntimeId, and allocation counts. These probes report; they do not assert undocumented pointer reuse or exact timing across Windows versions.

## 8. Windows Composed Graph and Capture

Verify:

- `Focused`, `Pointer`, `Point`, and `NativeWindow` with Direct resolution produce UIA-backed elements;
- `Default + Direct` produces the UI Automation desktop root;
- `Default + TopLevel` produces the first eligible top-level window in global Z-order;
- `Default + Screen` and Point or Pointer Screen resolution produce Win32-backed Screen elements;
- TopLevel resolution returns the same canonical top-level UIA element reached through native graph composition;
- NativeWindow or Focused Screen resolution returns the same canonical Screen reached through the TopLevel Parent relation;
- a top-level UIA window's Parent is the matching Screen;
- Screen children lazily return eligible top-level UIA windows;
- inverse traversal returns the same canonical top-level element while retained;
- top-level sibling order follows Screen spatial child order;
- displays sort top-to-bottom then left-to-right;
- `WM_DISPLAYCHANGE` creates a new immutable topology generation;
- old-generation Screen elements and Enumerators fail unavailable rather than retargeting a recycled handle;
- Screen process/native-window fields are missing rather than sentinel values;
- Screen `HMONITOR` release is a physical no-op but logical identity still expires;
- capture size/stride/pixel format describe physical pixels;
- `IVisualElementCapture` exposes no DPI and the Avalonia adapter attaches 96-DPI presentation metadata.

The test does not claim that display topology is immutable between notification and query; it verifies the accepted best-effort generation boundary.

## 9. Windows Actions

### 9.1 Standard Default Invocation

Verify:

- Invoke, Toggle, SelectionItem, ExpandCollapse, and LegacyIAccessible Patterns are requested in one operation-local cache;
- the first available mechanism runs in accepted precedence order;
- absence advances, while an attempted Pattern failure terminates without another Pattern or click;
- Expanded collapses; Collapsed and PartiallyExpanded expand; LeafNode is unsupported by that mechanism;
- Pattern owners release on every path;
- LegacyIAccessible follows absent standard Patterns, terminates on either success or HRESULT failure, and never falls through to a duplicate click;
- clickable-point fallback occurs only after every standard and LegacyIAccessible mechanism is absent/unusable;
- the point is inside virtual-screen bounds and still belongs to the resolved root-owner;
- virtual-desktop normalization handles negative/non-primary coordinates;
- move/down/up are one complete tagged batch;
- Screen rejects semantic invocation;
- HRESULT success is not asserted as universal application-semantic success.

Controlled scenarios must cover ordinary, disabled, hidden, off-screen, already-selected, toggle-state, radio, menu, tree, disclosure, split-button, virtualized, provider-broken, and multiple-Pattern controls. Scenario callbacks should later acknowledge the actual state transition; a Mock action counter alone is not provider evidence.

### 9.2 SetText

Verify:

- ValuePattern, IsEnabled, and ValuePattern.IsReadOnly are cached together;
- enabled editable ValuePattern receives the complete replacement including empty and embedded-null strings;
- disabled/read-only states reject before SetValue;
- absent ValuePattern is unsupported;
- BSTR and Pattern owners balance on every path;
- SetText does not focus, use TextPattern, LegacyIAccessible, clipboard, or simulated typing;
- successful SetValue is not followed by hidden keyboard retry;
- later scalar query can observe Mock replacement through normal identity.

### 9.3 Focus and SendKeyGesture

Verify:

- Focus uses a temporary AddRef and calls UIA SetFocus on the actual element;
- Focus does not substitute a containing HWND via AttachThreadInput;
- shortcut accepts both modified gestures and ordinary keys;
- native handle lookup uses a bounded parent chain;
- foreground root-owner activation is verified before input;
- foreground refusal sends nothing;
- unwanted physically held modifiers release first;
- requested modifiers already down are preserved;
- missing modifiers press and release around the main key in reverse order;
- unsupported mapping, partial SendInput insertion, UIPI, and provider failure remain distinguishable;
- every injected event carries the fixed Everywhere magic;
- no Focus request becomes implicit input and no shortcut becomes implicit SetText.

### 9.4 Selected Text and Top-Level Bounds

Verify:

- absent TextPattern and degenerate text ranges advance to selected-child fallback;
- multiple selection ranges concatenate in provider order under one shared character budget;
- SelectionPattern precedes LegacyIAccessible when reading selected child elements;
- selected child labels prefer UIA Name, then Legacy Name and Value, and multiple labels use newline separators;
- range and selected-child enumeration share the character budget and the 256-part operation ceiling;
- every TextRange, selected element, collection, Pattern, CacheRequest, and operation-local element releases on success and failure;
- selected-text provider failures retain the standard timeout/unavailable conversion;
- top-level HWND bounds prefer a successful DWM extended-frame rectangle;
- descendant elements and failed DWM queries retain cached UIA BoundingRectangle semantics;
- minimized top-level capture remains recorded as a known limitation rather than being hidden by bounds substitution.

## 10. Input Guard

When implemented, verify:

- overlay is visible but excluded from the observed UIA tree;
- it is non-activating and mouse-through;
- physical keyboard/mouse and unrelated injected input are blocked while active;
- correctly tagged Everywhere injected input is allowed;
- UIA Pattern actions remain usable without a low-level tag;
- physical Escape requests cancellation;
- keys/buttons already held at entry do not remain logically stuck;
- hook callbacks perform no blocking work;
- hooks and overlay are removed after success, cancellation, timeout, setup failure, and unexpected exception;
- unavailable guard setup produces explicit degraded status only when best-effort reading is permitted.

Guard tests are independent of element retention. Ending a guard does not release a Snapshot or Agent turn.

## 11. macOS Native Verification

Run on macOS and record:

- `AXUIElementRef` equality from different acquisition paths;
- Create/Copy ownership and returned CoreFoundation types;
- effective `AXUIElementSetMessagingTimeout` scope;
- batch-attribute per-position errors;
- Value and ranged-text behavior;
- indexed child paging and mutation;
- destroyed and unresponsive provider behavior;
- permissions and coordinate conversion;
- NSScreen identity, capture, and display changes;
- descendant/TopLevel/Application Parent chain;
- multiple TopLevel windows per Application;
- multi-display and spanning-window cases;
- focused, key, minimized, hidden, sheet, panel, full-screen Space, and windowless Applications.

Do not accept cross-backend Parent/Child/sibling tests until the topology checkpoint in [04-PlatformRuntime](04-PlatformRuntime.md) is reviewed. Windows cross-compilation is not evidence of runtime correctness.

## 12. Snapshot

Verify:

Current deterministic mock coverage verifies repeated Parent observations for two core siblings, bounded preview continuation facts, per-node child limits over a 100,000-item virtual collection, operation-limit partial results, and Enumerator disposal. The remaining bullets continue to define acceptance for production cutover.

- Weighted BFS order, distances, weights, core priority, and deduplication remain characterized;
- every loop iteration commits a node, advances an Enumerator, or closes a branch;
- aggregate elapsed, operation, node, child, text, and provider-failure measures are monotonic;
- one huge text element retains only a bounded preview and honest continuation/status;
- Value-only providers remain valid under native timeout even if payload transfer is complete;
- document providers use ranged text where supported;
- one huge/virtualized child collection stops without realizing or counting everything;
- known scalar failure retains a skeleton;
- root/edge failure attaches status to the nearest representable boundary;
- repeated provider/PID failure can suppress only the affected branch during the observation;
- unrelated roots remain eligible;
- no automatic retry occurs;
- Snapshot owns every admitted element after transient Enumerator disposal;
- Snapshot disposal releases observed-but-unpublished elements;
- published elements survive through current-turn ownership.

## 13. Prompt Projection, Composite, and PromptNode

Verify:

- the merged builder performs no platform calls;
- shared roots coalesce deterministically;
- transparent containers preserve child order;
- fragmented text can compress into Composite without losing independently interactive descendants;
- Composite exposes multi-member semantics, bounded preview, observed member count, continuation, and exceptional status;
- Composite never aliases a source element ID or becomes actionable;
- the same Prompt renderer owns structural cost, escaping, attributes, status, and pruning evidence;
- allocation is progressive and cannot be consumed entirely by one huge region before root fairness policy applies;
- final result is bounded text, internally rendered through Prompting primitives rather than hand-assembled markup;
- admitted competing bodies receive progressive shares within a single root as well as across roots;
- short bodies release demand, while a lone long body can use remaining capacity;
- final text, published IDs, and continuation are fixed before the tool returns;
- required attribute-only compact elements remain present as self-closing nodes without placeholder content;
- compact attributes and child text are escaped, only delimiter-free nonempty values omit quotes, and ordered sparse state flags remain valueless;
- normal results omit `complete`, capabilities, implementation priority, and empty status;
- `observedMembers` describes retained Composite parts rather than every live descendant;
- omitted known visual information is status, while renderer omission remains Prompting metadata;
- validation rendering converges monotonically and publication commits only targets present in the final prompt;
- construction failure consumes no ID.

## 14. VisualQuery

Verify:

- tool description states bounded, incomplete, mutable-tree semantics;
- one structural query handles Element and Composite targets without requiring the Agent to choose Inspect versus Expand;
- every query enforces request/result limits;
- structural offsets are 1-based and root `next` identifies the following selected retained-member range; current observation failures remain explicit, allowing overlap or retry;
- relation/provider failure preserves partial items and does not report definitive exhaustion;
- unavailable IDs fail without heuristic re-anchoring;
- historical IDs promote into the active turn on successful lookup;
- a new call is the only retry boundary;
- Composite actions reject before platform code;
- text offsets are zero-based UTF-16 positions regardless of bounded-prefix or complete-Value acquisition; automatic page boundaries preserve surrogate pairs;
- any later search contract never claims exhaustive absence from an unbounded live tree;
- output remains PromptNode before rendering.

Current automated coverage includes Element queries, Composite member paging, invalid Element offsets, and the shared Snapshot/PromptNode pipeline. The explicit Windows native WebView probe loads a real HTTP/HTTPS page through WebView2, requires its UIA `Document` to appear after renderer accessibility is enabled, and saves the exact compact Agent projection for manual inspection. Equivalent macOS and Linux evidence remains platform work rather than an inference from this Windows result.

## 15. Scenario Coverage and Acceptance

The catalog must include realistic app prototypes rather than isolated control strips: chat, feed, document, settings, file browser, IDE, spreadsheet, dashboard, media/library, and mixed dialog/navigation surfaces. Each has common chrome, menus/toolbars, navigation, content, status, and interaction affordances plus generated detail.

Extreme shapes include:

- one element with approximately one million characters;
- one parent with a huge lazy/virtualized child count;
- deeply nested transparent containers;
- fragmented multilingual/RTL text;
- multiple roots and shared native ancestors;
- mutable ordering driven by deterministic `MoveNext` steps;
- provider timeout, unavailable, unsupported, and recovery sequences;
- duplicate platform identity through different native return objects.

Required tests remain deterministic and bounded. Explicit native/visual tests may require a visible desktop and must clean up exactly the process they launched. Build/test success is necessary but not sufficient: architecture review must confirm that evidence, ownership, and timeout claims match the real boundary being tested.
