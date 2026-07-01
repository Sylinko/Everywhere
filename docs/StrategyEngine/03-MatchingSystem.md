# Strategy Engine Spec: Matching System

## 1. Goal

The matching system decides which Strategies should be recommended for the user's current context.

It must be:

1. Deterministic enough for users to understand.
2. Safe enough to run during assistant activation.
3. Fast enough to avoid blocking the UI.
4. Rich enough to support visual tree matching and platform-specific context such as file manager selection.

The output should eventually be `IReadOnlyList<StrategyCandidate>`. Existing code may temporarily keep `IReadOnlyList<Strategy>` as a compatibility projection.

## 2. End-to-end Matching Pipeline

```text
1. Load enabled strategy sources
2. Parse into versioned StrategyDefinitionVx documents
3. Normalize documents into runtime Strategy objects
4. Validate and collect diagnostics during normalization
5. Analyze condition dependencies
6. Build base StrategyContext
7. Collect required ExtraContext subtrees
8. Evaluate conditions with bool? semantics
9. Filter strategies where result == true
10. Sort by priority
11. Emit diagnostics and slow-match notifications
```

## 3. Loading Enabled Strategy Sources

Providers return raw strategy sources:

```csharp
public interface IStrategyDocumentProvider
{
    string ProviderId { get; }
    IAsyncEnumerable<StrategyDocument> LoadAsync(CancellationToken cancellationToken);
}
```

Recommended provider responsibilities:

| Provider | Responsibility |
| --- | --- |
| Builtin provider | Return compiled Strategy definitions or embedded markdown resources. |
| User directory provider | Load user `.strategy.md` files. |
| Workspace provider | Load workspace-local `.strategy.md` files when workspace support exists. |
| Plugin/app provider | Return plugin contributed definitions when the plugin model supports this. |

Providers should not evaluate conditions. They only load definitions.

## 4. Normalization and `from` Resolution

If a Strategy file contains `from`, the normalizer resolves that source as part of compiling `StrategyDefinitionVx` into runtime `Strategy`.

This is intentionally include-like. It may look like copy-paste replacement in the final Strategy, but the implementation must preserve the include reference for diagnostics, navigation, cache invalidation, and future editor UI.

Normalization algorithm:

1. Parse current file frontmatter/body.
2. Select `IStrategyDefinitionNormalizer` by `schema`.
3. If `from` exists, resolve `from.source` using registered source resolvers.
4. Parse source into a source definition.
5. Apply current file as replace-overrides on top of source definition.
6. Convert the merged definition into runtime `Strategy`.
7. Store current source as `Strategy.Source`.
8. Store included source references in `Strategy.Includes`.
9. Validate and return diagnostics.

The normalizer should be hand-written. Do not hide this path behind AutoMapper-style runtime mapping. A source generator may assist mechanical field copying, but source resolution, defaulting, validation, condition compilation, icon/duration parsing, namespace assignment, and diagnostics belong in the normalizer.

Source resolver interface:

```csharp
public interface IStrategySourceResolver
{
    bool CanResolve(StrategyFromReference reference, StrategySource currentSource);
    Task<StrategyDocument> ResolveAsync(
        StrategyFromReference reference,
        StrategySource currentSource,
        CancellationToken cancellationToken);
}
```

v1 required resolvers:

1. Relative file resolver.
2. Absolute file resolver.
3. Managed skill resolver (`skill://id`) once import UI exists.

URL resolver is interface-ready but may be disabled in v1.

Cycle handling:

1. `from` depth greater than 1 is invalid in v1.
2. If source is another `.strategy.md` with its own `from`, reject it in v1.
3. The diagnostic should point to both the current file and the included file when possible.

## 5. Validation

Validation must happen before matching.

Required validation errors:

1. Missing or invalid `id` after provider namespace assignment.
2. User-authored file attempts to allocate `builtin.*`.
3. Duplicate ID within one provider.
4. Invalid YAML frontmatter.
5. Invalid `when` structure.
6. Unknown root condition operator.
7. Invalid `options` duration.
8. Unknown preprocessor ID if the registry is available at validation time.
9. Invalid `tools` value type.
10. Invalid visual query syntax.

Validation diagnostics should include file path, line/column when available, and a user-readable explanation.

Invalid user Strategies must be skipped. Invalid builtin Strategies should be treated as developer errors and logged prominently.

## 6. Static Dependency Analysis

The engine infers required context by walking the `when` tree.

Example:

```yaml
when:
  all:
    - extra.file_manager.selection.items:
        count: { min: 1 }
    - clipboard.text:
        contains: "TODO"
    - visual.exists:
        query: "//ListViewItem[@selected=true]"
```

Dependency result:

```text
extra.file_manager
clipboard.text
visual
```

No separate `requires` section is needed. If a condition references `extra.browser.active_tab.url`, the engine should infer that an ExtraContext provider capable of `extra.browser` may be needed.

Recommended representation:

```csharp
public sealed record StrategyContextRequirements
{
    public bool NeedsClipboard { get; init; }
    public bool NeedsVisualTree { get; init; }
    public IReadOnlySet<string> ExtraRoots { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> AssistantPaths { get; init; } = new HashSet<string>();
}
```

Extra roots should be coarse enough to avoid running too many providers:

```text
extra.file_manager
extra.browser
extra.workspace
```

## 7. Base StrategyContext Collection

Base context is collected from data already available to Everywhere or cheap platform APIs:

1. Attachments.
2. Root visual elements derived from attachments.
3. Active process derived from visual elements or focused window.
4. Clipboard, only if required.
5. Current selected assistant/model information.
6. Environment information.

The existing `StrategyContext.FromAttachments(...)` can be retained but should be expanded or wrapped so matching is not attachment-only.

Recommended target:

```csharp
public interface IStrategyContextFactory
{
    Task<StrategyContext> CreateAsync(
        StrategyContextRequirements requirements,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);
}
```

## 8. ExtraContext Collection

ExtraContext providers fill the `extra` subtree. Authors write cross-platform paths like:

```text
extra.file_manager.selection.items
```

The engine chooses a provider based on platform and current active context.

Recommended interface:

```csharp
public interface IExtraContextProvider
{
    string Id { get; }                         // windows.explorer, macos.finder
    string PublicRoot { get; }                 // extra.file_manager
    IDynamicLocaleKey PermissionDescriptionKey { get; }

    bool CanCollect(StrategyContext baseContext, ExtraContextRequest request);

    Task<ExtraContextNode?> CollectAsync(
        StrategyContext baseContext,
        ExtraContextRequest request,
        CancellationToken cancellationToken);
}
```

Rules:

1. Multiple providers may serve one public root.
2. The engine chooses providers by `CanCollect`.
3. Provider ID is not exposed in the public DSL.
4. Provider ID may be stored in diagnostics.
5. Provider failure returns `null` for its subtree and a diagnostic entry.
6. Provider timeout returns `null`.
7. Missing data does not throw during condition evaluation.

## 9. File Manager Provider Semantics

Public root:

```text
extra.file_manager
```

Windows implementation:

1. Detect active File Explorer window from active process/window handle.
2. Use Shell COM to find the matching Explorer window.
3. Read current folder and selected shell items.
4. Convert shell items to the public item model.
5. Preserve virtual items with `kind: virtual` and `path: null`.

macOS implementation:

1. Detect active Finder.
2. Use Finder scripting/Apple Events to read current selection when permission is available.
3. Convert Finder items to the public item model.
4. If automation permission is unavailable, return `null` and a permission diagnostic.

The matching DSL should not mention `windows.explorer` or `macos.finder` unless a future platform-specific extra root is intentionally introduced.

## 10. Three-valued Condition Evaluation

All conditions return `bool?`.

### 10.1 Root Recommendation Rule

```text
show strategy if and only if root condition == true
```

`false` and `null` both hide the Strategy from the recommendation list.

### 10.2 Composite Operators

`all`:

```text
if any child is false -> false
else if all children are true -> true
else -> null
```

`any`:

```text
if any child is true -> true
else if all children are false -> false
else -> null
```

`none`:

```text
let r = any(children)
if r == true -> false
if r == false -> true
if r == null -> null
```

This prevents missing data from accidentally satisfying negative checks.

## 11. Field Conditions

A field condition maps a path to an operator object.

```yaml
attachments.selection.text:
  length:
    min: 1
```

Evaluation steps:

1. Resolve the path against `StrategyContext`.
2. If path is unavailable or provider returned null, return `null`.
3. Apply the operator.
4. If operator cannot be applied to the value type, return `false` and a diagnostic.
5. Respect per-condition timeout.

## 12. String Operators

Supported operators:

```yaml
equals: "chrome"
in: ["chrome", "msedge"]
contains: "invoice"
startsWith: "https://"
endsWith: ".pdf"
regex: "\\bTODO\\b"
glob: "*.pdf"
caseSensitive: true
```

Rules:

1. String operations default to case-insensitive.
2. `caseSensitive: true` applies to the condition object.
3. Regex must use `options.regexTimeout`.
4. Regex timeout returns `null` and emits a diagnostic.
5. Invalid regex is validation error when possible; otherwise it evaluates `false` with diagnostic.

## 13. Numeric and Length Operators

For numbers:

```yaml
size:
  min: 1024
  max: 10485760
```

For string length:

```yaml
length:
  min: 1
  max: 5000
```

For arrays:

```yaml
count:
  min: 1
  max: 10
```

If value type does not support the operator, return `false`.

## 14. Array Operators

Arrays support:

```yaml
count:
  min: 1

any:
  extension:
    in: [".pdf", ".docx"]

all:
  kind:
    equals: "file"

none:
  extension:
    in: [".exe", ".bat"]
```

Array item object matching rules:

1. Each key under `any/all/none` is interpreted as a relative path from the item.
2. `any` is true if at least one item matches.
3. `all` is true only if all items match and the array is not empty.
4. `none` follows the same three-valued semantics as condition-level `none`.
5. Empty arrays are valid values, not `null`.

## 15. Visual Query Conditions

v1 supports exactly three visual conditions:

```yaml
visual.exists:
  query: "//TopLevel//ListViewItem[@selected=true]"

visual.count:
  query: "//ListViewItem[@selected=true]"
  min: 1
  max: 5

visual.match:
  query: "//TopLevel/@name"
  contains: "Visual Studio"
```

Evaluation:

1. If visual context is unavailable, return `null`.
2. If query syntax is invalid, validation error.
3. If query times out, return `null`.
4. `visual.exists` returns `true` if at least one element matches, otherwise `false`.
5. `visual.count` returns `true` if result count is within range, otherwise `false`.
6. `visual.match` selects attribute values and applies normal operators.

## 16. Visual Query DSL Scope

The visual query DSL is XPath-like, not XPath.

Supported:

| Syntax | Meaning |
| --- | --- |
| `//Button` | Any descendant with visual type `Button`. |
| `/TopLevel/Panel/Button` | Strict parent-child path. |
| `.` | Current primary visual attachment. |
| `*` | Any visual type. |
| `@name` | Read element name. |
| `@text` | Read element text. |
| `@process` | Read process name. |
| `@selected` | Read selected state. |
| `@focused` | Read focused state. |
| `@disabled` | Read disabled state. |
| `@readonly` | Read read-only state. |
| `@offscreen` | Read offscreen state. |
| `@password` | Read password state. |
| `@bounds.width` | Read bounds width. |
| `[@selected=true]` | Boolean filter. |
| `[@name='Save']` | Equality filter. |
| `contains(@name,'Save')` | Contains filter. |
| `matches(@text,'error|warning')` | Regex filter. |

Not supported in v1:

1. Full XPath axes.
2. XML namespaces.
3. Arbitrary functions.
4. Arithmetic expressions.
5. Unstable index selectors like `//Button[3]`.
6. Platform-specific properties not in the cross-platform field set.

`@text` can be expensive. It must only be read when explicitly referenced, and must respect text length limits and timeouts.

## 17. Sorting

Matched Strategies are sorted by:

1. `priority` descending.
2. Provider order only as a deterministic tie-breaker.
3. Name or ID as final stable tie-breaker.

No score-based matching in v1. Matching is boolean.

## 18. Timeouts

Timeouts come from `options`, with global defaults.

Recommended defaults:

```yaml
options:
  matchingTimeout: 300ms
  conditionTimeout: 80ms
  regexTimeout: 50ms
  visualQueryTimeout: 120ms
  extraTimeout: 200ms
```

Rules:

1. `matchingTimeout` is the total budget for one Strategy evaluation.
2. `conditionTimeout` is the default budget for one condition node.
3. `regexTimeout` applies per regex operation.
4. `visualQueryTimeout` applies per visual query.
5. `extraTimeout` applies per ExtraContext provider call.
6. Timeout returns `null`, not `false`.

## 19. Slow Strategy Notifications

If one or more Strategies are skipped due to timeout or slow matching, the normal UI should show a Toast.

Suggested user text:

```text
Some strategies took too long to check and were skipped.
```

Requirements:

1. Ordinary users see a concise Toast.
2. Strategy Diagnostics shows exact Strategy IDs, durations, timeout nodes, and provider IDs.
3. Toasts should be rate-limited to avoid noise.
4. Slow builtin Strategies should be logged as warnings.

## 20. Diagnostics

Diagnostics should be structured:

```csharp
public sealed record StrategyDiagnostic
{
    public required StrategyDiagnosticSeverity Severity { get; init; }
    public required string Code { get; init; }
    public IDynamicLocaleKey? MessageKey { get; init; }
    public string? Path { get; init; }
    public string? ProviderId { get; init; }
    public TimeSpan? Duration { get; init; }
    public Exception? Exception { get; init; }
}
```

Common diagnostic codes:

```text
strategy.invalid_yaml
strategy.invalid_from
strategy.unknown_preprocessor
condition.path_missing
condition.type_mismatch
condition.timeout
regex.invalid
regex.timeout
visual.query_invalid
visual.query_timeout
extra.provider_unavailable
extra.provider_timeout
extra.permission_unavailable
```

Diagnostics must not leak sensitive text content by default. Use path names and summaries, not full clipboard/file contents.
