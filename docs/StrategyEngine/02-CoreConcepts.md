# Strategy Engine Spec: Core Concepts

## 1. Runtime Objects

The Strategy Engine must normalize all sources into a small set of runtime objects. The names below are specification names; implementation may split records/classes as needed, but public behavior must remain equivalent.

```text
StrategyDefinitionV1
StrategySource
StrategyDocument
StrategyNormalizationResult
StrategyContext
ExtraContextSnapshot
StrategyCondition
ConditionEvaluationResult
StrategyCandidate
PreprocessorResult
StrategyExecutionPlan
```

## 2. StrategyDefinitionV1 and Strategy

Strategy Engine intentionally separates the versioned authoring model from the runtime model.

`StrategyDefinitionV1` is the versioned authoring model for `schema: everywhere.strategy/v1`. It is close to the YAML file format and may contain `from`, string durations, string icon names, partially specified options, raw condition structures, and authoring-time metadata.

`Strategy` is the runtime model. It has no schema version and is optimized for matching/execution: parsed durations, parsed icon values, compiled condition AST, normalized tool rules, resolved preprocessor IDs, source references, and diagnostics.

This split is required because Strategy files are versioned. Future breaking format changes should add another authoring model and normalizer:

```text
StrategyDefinitionV1 -> Strategy
StrategyDefinitionV2 -> Strategy
StrategyDefinitionV3 -> Strategy
```

Do not use `Dto` suffixes in code. These are versioned definitions, not generic data-transfer objects.

Recommended authoring model shape:

```csharp
public sealed record StrategyDefinitionV1
{
    public string Schema { get; init; } = "everywhere.strategy/v1";
    public string? Id { get; init; }
    public StrategyFromReference? From { get; init; }
    public bool? Enabled { get; init; }

    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public int? Priority { get; init; }

    public object? When { get; init; }
    public string? Body { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyDictionary<string, bool>? Tools { get; init; }
    public IReadOnlyList<string>? Preprocessors { get; init; }
    public StrategyOptionsDefinitionV1? Options { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}
```

Recommended runtime model shape:

```csharp
public sealed partial record Strategy
{
    public required string Id { get; init; }
    public required StrategySource Source { get; init; }
    public IReadOnlyList<StrategySource> Includes { get; init; } = [];
    public bool Enabled { get; init; } = true;

    public required IDynamicLocaleKey NameKey { get; init; }
    public IDynamicLocaleKey? DescriptionKey { get; init; }
    public ColoredIcon? Icon { get; init; }
    public int Priority { get; init; }

    public IStrategyCondition? Condition { get; init; }
    public string? Body { get; init; }
    public string? SystemPrompt { get; init; }
    public ToolPatternRulesets? ToolPatternRulesets { get; init; }
    public IReadOnlyList<string> Preprocessors { get; init; } = [];
    public StrategyOptions Options { get; init; } = StrategyOptions.Default;

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}
```

Compatibility note: the existing `Strategy` type can evolve toward this runtime model. Because Strategy serialization is not yet established, prefer adapting the current `Strategy` rather than creating a parallel runtime definition type.

## 3. StrategyDocument

`StrategyDocument` is the parsed source file before normalization.

It should preserve:

1. Source location.
2. Raw frontmatter text or parsed definition.
3. Raw markdown body.
4. Schema value.
5. Parse diagnostics.
6. Source spans when the YAML library exposes them.

Recommended shape:

```csharp
public sealed record StrategyDocument
{
    public required StrategySource Source { get; init; }
    public required string Schema { get; init; }
    public required object Definition { get; init; }
    public string? Body { get; init; }
    public bool HasBodySection { get; init; }
    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}
```

## 4. StrategySource

`StrategySource` describes where a Strategy came from. It is used for diagnostics, reload behavior, and stable ID generation.

```csharp
public sealed record StrategySource
{
    public required string ProviderId { get; init; }      // builtin, user, workspace, plugin.*
    public required Uri Location { get; init; }           // file path, resource URI, URL, skill URI
    public string? SourceHash { get; init; }
    public bool IsBuiltin { get; init; }
}
```

Provider IDs are implementation details except when shown in diagnostics. Strategy authors should not have to know whether `extra.file_manager.*` was supplied by `windows.explorer` or `macos.finder`.

## 5. Strategy Normalization

Normalization is a hand-written compile step from a versioned definition to the runtime `Strategy`.

Do not introduce AutoMapper-style runtime mapping for this path. The conversion is not mechanical field copying; it includes `from` resolution, defaulting, validation, parsing, condition compilation, diagnostics, and namespace assignment. A source generator may be used for small mechanical substeps, but the normalizer remains the semantic boundary.

Recommended interface:

```csharp
public interface IStrategyDefinitionNormalizer
{
    string Schema { get; }

    Task<StrategyNormalizationResult> NormalizeAsync(
        StrategyDocument document,
        StrategyLoadContext context,
        CancellationToken cancellationToken);
}
```

Recommended result:

```csharp
public sealed record StrategyNormalizationResult
{
    public Strategy? Strategy { get; init; }
    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}
```

Normalizer responsibilities:

1. Resolve and apply `from`.
2. Apply schema defaults.
3. Assign provider namespace and final ID.
4. Reject invalid or reserved IDs.
5. Parse icon names to runtime icon values.
6. Parse durations to `TimeSpan`.
7. Compile condition DSL into condition AST.
8. Parse visual queries.
9. Convert `tools` into `ToolPatternRulesets`.
10. Validate preprocessor IDs when registry is available.
11. Preserve include/source references for diagnostics.
12. Produce diagnostics instead of throwing for ordinary user-file errors.

## 6. Optional Fields and Presence

Most fields do not need to distinguish "not set" from "explicit null" in v1.

Recommended simple rule:

1. If a field is absent, it inherits from `from` source or receives a default.
2. If a field is present with a concrete value, it replaces the source/default.
3. Explicit null is invalid for most scalar fields unless a later schema explicitly defines null semantics.
4. Empty body means an intentionally empty body only when a body section is present.
5. Missing body section inherits source body.

If a future schema needs precise presence tracking, introduce an `Optional<T>` or equivalent wrapper in the versioned definition model. Do not add this complexity before a real field needs it.

## 7. Provider Types

The implementation should support these provider categories:

| Provider | Purpose | v1 Requirement |
| --- | --- | --- |
| `builtin` | Built-in strategies compiled into Everywhere. | Required. |
| `user` | User-managed `.strategy.md` files. | Required. |
| `workspace` | Project/workspace-local strategies. | Optional in v1, architecture-ready. |
| `plugin.*` / `app.*` | Strategies contributed by future plugins/apps. | Optional in v1, architecture-ready. |

Builtin Strategies must not be overridden by user Strategies. If a user wants a different behavior, the user creates another Strategy with a user-owned ID and higher priority. UI may allow disabling builtin Strategies separately, but that is not the same as overriding the builtin definition.

## 8. ID Rules

Every Strategy must have a stable ID.

Recommended ID format:

```text
<provider-or-owner>.<domain>.<name>
```

Examples:

```text
builtin.global.help
builtin.browser.summarize
user.file-manager.summarize-selection
workspace.repo.review-current-file
```

Rules:

1. IDs are case-insensitive for lookup and conflict checks.
2. `builtin` is a namespace assigned by the builtin provider, not by user-authored files.
3. User-authored files cannot allocate themselves into `builtin.*` because provider namespace assignment happens during normalization.
4. If a user file writes `id: builtin.foo`, the user provider must reject it or normalize it under the user namespace. Prefer rejection with a clear diagnostic.
5. If a file omits `id`, the provider may derive one from its relative path.
6. Duplicate IDs inside the same provider namespace are validation errors.
7. Duplicate IDs across provider namespaces are distinct unless explicitly bridged by a later feature. v1 should avoid cross-provider override entirely.

## 9. `from` Source Reference

`from` means the current Strategy definition includes exactly one source definition. It looks like replacement/copying at the authoring level, but the implementation must preserve the reference for diagnostics, source navigation, caching, and future editor UI. The mental model is closer to C/C++ `#include` than destructive copy-paste.

Short form:

```yaml
from: ./SKILL.md
```

Expanded form:

```yaml
from:
  source: ./SKILL.md
  kind: auto
```

Supported source forms:

| Form | Example | Requirement |
| --- | --- | --- |
| Relative path | `./SKILL.md` | Required. Resolved relative to current strategy file. |
| Absolute path | `E:\Skills\Writer\SKILL.md` | Required. |
| Managed skill URI | `skill://everywhere.my-writing-style` | Required once skill import UI exists. |
| URL | `https://example.com/strategy.md` | Interface-ready; implementation may block network until later. |

Supported `kind` values:

| Kind | Meaning |
| --- | --- |
| `auto` | Infer by extension/name/content. Default. |
| `skill` | Parse as `SKILL.md` style markdown. |
| `strategy` | Parse as `.strategy.md`. |
| `markdown` | Treat as plain prompt body. |
| `url` | Resolve using a URL-capable source resolver. |

Include and override semantics:

1. Only one `from` is allowed.
2. The base source is parsed and normalized enough to expose its definition.
3. Fields in the current file replace source fields by default.
4. If current file has a body section, it replaces the source body.
5. If current file has no body section, it inherits the source body.
6. The final runtime `Strategy.Source` points to the current file.
7. The base source is retained in `Strategy.Includes` and diagnostics.
8. `id` and source identity are not blindly inherited; the final Strategy must still be assigned in the current provider namespace.
9. No multiple inheritance and no merge list semantics in v1.

## 10. StrategyContext

`StrategyContext` is the input to matching and execution.

It should be organized as:

```text
attachments
visual
clipboard
assistant
environment
extra
metadata
```

### 7.1 Attachments

Attachments are user-visible inputs already known to Everywhere.

User-facing DSL paths:

```text
attachments.files
attachments.selection.text
attachments.text
attachments.visual.primary
attachments.visual.items
```

The DSL should not expose implementation class names like `TextSelectionAttachment`.

### 7.2 Visual

`visual` is the UI tree search surface. It is not the serialized prompt representation. It is used by `visual.exists`, `visual.count`, and `visual.match`.

The cross-platform field set for v1 is limited to fields already available or reasonably derivable from `IVisualElement`:

```text
type
name
text
states
process
nativeWindowHandle
bounds
```

Do not require v1 to expose platform-specific fields such as AutomationId, ClassName, FrameworkId, AXIdentifier, or control pattern details.

### 7.3 Clipboard

Clipboard can be read before matching. Because Everywhere is a local desktop assistant, this is acceptable for v1. The Strategy details UI should still disclose when a Strategy references clipboard paths.

Example DSL paths:

```text
clipboard.text
clipboard.hasText
clipboard.hasImage
clipboard.files
```

### 7.4 Assistant

`assistant` exposes the currently selected assistant and model capabilities.

Example DSL paths:

```text
assistant.id
assistant.name
assistant.model.id
assistant.model.modalities
assistant.model.supportsToolCall
```

### 7.5 Environment

`environment` contains stable, cheap information.

Example DSL paths:

```text
environment.os
environment.architecture
environment.locale
environment.timeZone
environment.currentDate
```

### 7.6 Extra

`extra` is the user-facing name for additional context collected on demand.

Implementation names:

```text
ExtraContextSnapshot
IExtraContextProvider
ExtraContextPath
```

Example paths:

```text
extra.file_manager.current_folder.path
extra.file_manager.selection.items
extra.browser.active_tab.url
extra.browser.active_tab.title
extra.workspace.root
extra.workspace.git.branch
```

The engine must infer required ExtraContext providers by statically analyzing `when` paths. Authors do not need a separate `requires` section.

If an `extra` path is missing, unavailable, unsupported on the current platform, denied, or timed out, the corresponding condition evaluates to `null`.

## 11. File Manager Extra Context

The public schema must be cross-platform:

```yaml
extra:
  file_manager:
    current_folder:
      path: string?
      displayName: string?
    selection:
      items:
        - path: string?
          displayName: string
          kind: file | folder | virtual | unknown
          extension: string?
          size: long?
```

Implementation details:

1. Windows may populate this from File Explorer through Shell COM.
2. macOS may populate this from Finder through Apple Events / scripting interfaces.
3. Linux may later populate this from known file managers when feasible.
4. Provider identity is omitted from the public schema and inferred internally.
5. Virtual/non-filesystem shell items must not be discarded; use `kind: virtual` and `path: null`.

## 12. Condition Result

All conditions return `bool?`:

| Value | Meaning |
| --- | --- |
| `true` | The condition clearly matches. |
| `false` | The condition clearly does not match. |
| `null` | The condition could not be evaluated: missing data, timeout, unsupported platform, provider failure, permission not available. |

The final Strategy is recommended only if the root `when` evaluates to `true`. `false` and `null` both mean "do not show", but diagnostics must preserve the difference.

## 13. StrategyCandidate

`StrategyCandidate` is the output of matching. Existing code currently returns `IReadOnlyList<Strategy>`; v1 may keep that API temporarily, but the target model should include evaluation metadata.

```csharp
public sealed record StrategyCandidate
{
    public required Strategy Strategy { get; init; }
    public required bool IsMatched { get; init; }
    public ConditionEvaluationResult? Evaluation { get; init; }
    public TimeSpan EvaluationDuration { get; init; }
    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}
```

Only matched candidates are shown in the normal UI. Unmatched and null-result candidates should be available in a diagnostics view.

## 14. ToolPatternRulesets

`ToolPatternRulesets` separates plugin matching from function matching.

```yaml
tools:
  builtin.web:
    "*": true
    web_search: false
  builtin.file_system:
    read_file: true
```

Semantics:

1. Outer keys are plugin-key glob patterns.
2. Inner keys are function-name glob patterns.
3. Inner values are allow/deny booleans.
4. Later/stronger rule sources override earlier sources through `ToolRulesetsPipeline`.
5. Strategy tool rules are applied to the user request that executes that Strategy.

## 15. Permissions

The engine should be able to report permissions implied by:

1. `when` paths, including `clipboard.*`, `visual.*`, and `extra.*`.
2. Preprocessors.
3. Tool rules that enable tools with permissions.

Permission display must use `IDynamicLocaleKey` so the UI can show natural language text:

```text
Reads clipboard text
Reads the current file manager folder and selection
Reads visible UI element information
Allows file read tool
Allows web search tool
```

This spec does not require v1 to block matching on new consent UI. It requires v1 to expose enough metadata for a Strategy details UI and future permission controls.

## 16. PreprocessorResult

Preprocessors are predefined components referenced by ID. v1 preprocessors return variables only.

Recommended result:

```csharp
public sealed record PreprocessorResult
{
    public IReadOnlyDictionary<string, object?> Variables { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}
```

Variables use path-style names:

```text
preprocess.browser.readable_text
preprocess.file_manager.selected_file_text
extra.file_manager.selection.items
attachments.selection.text
```

Prompt interpolation uses the same path style:

```markdown
{attachments.selection.text}
{extra.file_manager.selection.items}
{preprocess.browser.readable_text}
```

## 17. Options

All tunable runtime behavior belongs under `options`.

```yaml
options:
  matchingTimeout: 300ms
  conditionTimeout: 80ms
  regexTimeout: 50ms
  visualQueryTimeout: 120ms
  extraTimeout: 200ms
```

Field names and default values are defined in `05-ConfigurationFormat.md`.
