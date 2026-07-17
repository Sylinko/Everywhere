# Strategy Engine Spec: Implementation Plan

## 1. Goal

This document turns the Strategy Engine spec into an implementation sequence suitable for an agent or engineer.

The implementation should be incremental. Do not attempt to replace the existing Strategy Engine in one large change. Preserve current builtin Strategy behavior while introducing the new file format, matching model, and execution hooks.

## 2. Recommended Milestones

```text
M1. Domain model and versioned definitions
M2. SharpYaml strategy parser and validation
M3. Hand-written normalizer and `from` source resolution
M4. Skills v1 registry and complete `skill://{source}.{skill}` resolution
M5. Condition DSL and three-valued evaluator
M6. ExtraContext provider pipeline
M7. Visual query DSL subset
M8. Preprocessor execution pipeline
M9. UI/diagnostics integration
M10. Migration cleanup and tests
```

Each milestone should be independently testable.

## 3. M1: Domain Model and Versioned Definitions

### 3.1 Add or evolve model types

Introduce target model types without breaking existing UI:

```text
StrategyDefinitionV1
StrategyDocument
StrategyNormalizationResult
StrategySource
StrategyFromReference
StrategyOptions
StrategyCandidate
ConditionEvaluationResult
StrategyDiagnostic
ExtraContextSnapshot
ExtraContextNode
StrategyExecutionContext
```

The existing `Strategy` record should evolve into the runtime model. Because Strategy serialization is not yet established, avoid creating a parallel runtime type if the current `Strategy` can be adapted cleanly.

Required distinction:

```text
StrategyDefinitionV1 = versioned authoring model parsed from YAML
Strategy = runtime model used by matching, UI, execution, and chat messages
```

Do not use `Dto` suffixes. Do not introduce AutoMapper as the semantic conversion layer.

### 3.2 Acceptance criteria

1. Existing builtin Strategies still appear.
2. Existing selected Strategy can still be sent as `UserStrategyChatMessage`.
3. No user file loading yet.
4. Unit tests cover default values and runtime `Strategy` construction.

## 4. M2: SharpYaml Strategy Parser and Validation

### 4.1 Parser

Implement a parser for Markdown + YAML frontmatter.

Use SharpYaml for YAML serialization/deserialization. Prefer source-generated metadata where practical. v1 does not support comment-preserving roundtrip; Strategy Editor may rewrite frontmatter into canonical formatting.

Input:

```text
path to .strategy.md
raw markdown content
provider id
```

Output:

```text
StrategyDocument
StrategyDefinitionV1
diagnostics
```

The parser must not execute code in the file.

### 4.2 YAML mapping

Map all v1 fields:

```text
schema
id
from
enabled
name
description
icon
priority
when
tools
preprocessors
systemPrompt
options
body
```

### 4.3 Validation

Implement validation from `05-ConfigurationFormat.md`.

Invalid user files should:

1. Be skipped during matching.
2. Appear in diagnostics.
3. Not crash the app.

### 4.4 Acceptance criteria

1. Valid `.strategy.md` files parse into definitions.
2. Invalid YAML returns diagnostics.
3. Invalid `tools` shape returns diagnostics.
4. Invalid duration returns diagnostics.
5. Parser round-trips body exactly except normalized line endings.

## 5. M3: Hand-written Normalizer and `from` Source Resolution

### 5.1 Implement normalizer

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

The normalizer owns:

1. `from` resolution.
2. Replace-only override application.
3. Defaults.
4. Provider namespace assignment.
5. ID validation.
6. Icon parsing.
7. Duration parsing.
8. Condition AST compilation.
9. Visual query parsing.
10. ToolRulesets construction.
11. Preprocessor ID validation.
12. Diagnostics.

Do not use AutoMapper-style runtime mapping for this path. Field count is small, and the conversion is closer to compilation than object copying.

### 5.2 Implement source resolver interfaces

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

Required resolvers:

1. Relative file resolver.
2. Absolute file resolver.
3. Managed skill resolver, if a skill registry/import store exists.

URL resolver should be interface-ready but may return unsupported until a network policy is defined.

### 5.3 Include and merge semantics

Implement replace-only override:

1. Current field replaces source field.
2. Current body replaces source body if a body section is present.
3. Missing body section inherits source body.
4. Final `Strategy.Source` points to current file.
5. Included source is preserved in `Strategy.Includes` and diagnostics.
6. Nested `from` rejected in v1.

### 5.4 Acceptance criteria

1. Strategy can derive body from `./SKILL.md`.
2. Strategy can override name/icon/priority/when/tools.
3. Current body replaces source body.
4. Missing current body inherits source body.
5. Nested `from` produces validation diagnostic.
6. Included source remains visible in diagnostics.

## 6. M4: Skills v1 Registry and Complete Skill URI Resolution

Skills v1 is deliberately small. It is not another Strategy Engine.

Implement:

1. Automatic discovery of local skills.
2. A user management UI similar to ChatPlugin settings.
3. Enabled/disabled state persisted by skill ID.
4. System prompt injection of enabled skill index.
5. Complete `skill://{source}.{skill}` resolver for Strategy `from`.

Do not implement:

1. Skill auto-triggering based on context.
2. Skill condition matching.
3. Embedding or semantic skill selection.
4. Slash commands, except as a future extension.

Suggested model:

```csharp
public sealed record SkillDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string FilePath { get; init; }
    public string SourceKind { get; init; } = "user";
    public bool Enabled { get; init; }
}
```

Prompt injection should include only an index, not full skill contents:

```text
Available skills:
- writing.polite: Polite writing style.
  URI: skill://everywhere.writing-polite/SKILL.md
- coding.review: Code review guidance.
  URI: skill://everywhere.coding-review/SKILL.md
```

The enabled skill index must include each skill's complete `skill://{source}.{skill}/SKILL.md` URI. The model reads skill content by calling `read_file` with that URI. Short IDs and source-slash forms are invalid.

Acceptance criteria:

1. Skills are discovered from configured roots.
2. User can enable/disable a skill.
3. Enabled skills appear in system prompt index with full `SKILL.md` paths.
4. Disabled skills do not appear in system prompt index.
5. `from: skill://{source}.{skill}` resolves an enabled or installed skill according to policy.

## 7. M5: Condition DSL and Three-valued Evaluator

### 6.1 Build condition AST

Represent:

```text
true
false
all
any
none
path condition
visual condition
```

Path condition stores:

```text
path
operator object
source location
```

### 6.2 Implement `bool?` evaluation

Rules:

```text
all: false beats null; all true => true; otherwise null
any: true beats null; all false => false; otherwise null
none: not(any), preserving null
```

Only root `true` recommends the Strategy.

### 6.3 Implement operators

Required operators:

```text
equals
in
contains
startsWith
endsWith
regex
glob
caseSensitive
length
count
min
max
any
all
none
```

### 6.4 Acceptance criteria

1. Missing path returns `null`.
2. Type mismatch returns `false` with diagnostic.
3. Regex timeout returns `null`.
4. `none` over missing path returns `null`, not `true`.
5. Array `any/all/none` works on object item relative paths.

## 8. M6: ExtraContext Provider Pipeline

### 7.1 Static dependency analyzer

Walk condition AST and infer required roots:

```text
extra.file_manager
extra.browser
extra.workspace
clipboard
assistant
visual
```

No user-authored `requires` section.

### 7.2 Provider interface

```csharp
public interface IExtraContextProvider
{
    string Id { get; }
    string PublicRoot { get; }
    IDynamicLocaleKey PermissionDescriptionKey { get; }
    bool CanCollect(StrategyContext baseContext, ExtraContextRequest request);
    Task<ExtraContextNode?> CollectAsync(
        StrategyContext baseContext,
        ExtraContextRequest request,
        CancellationToken cancellationToken);
}
```

### 7.3 File manager provider

Public root:

```text
extra.file_manager
```

Windows provider:

```text
Id: windows.explorer
PublicRoot: extra.file_manager
```

Target fields:

```text
current_folder.path
current_folder.displayName
selection.items[].path
selection.items[].displayName
selection.items[].kind
selection.items[].extension
selection.items[].size
```

Implementation notes:

1. Use active/focused window information to detect File Explorer.
2. Use Shell COM to obtain current folder and selection.
3. Preserve virtual items.
4. Run on appropriate apartment/threading model.
5. Enforce `extraTimeout`.

macOS provider:

```text
Id: macos.finder
PublicRoot: extra.file_manager
```

Implementation notes:

1. Use Finder scripting/Apple Events when available.
2. Return permission diagnostic if automation permission is missing.
3. Fill the same public schema.

### 7.4 Acceptance criteria

1. `extra.file_manager.selection.items` works on supported Windows Explorer scenarios.
2. Unsupported platform returns `null`, not exception.
3. Provider timeout returns `null` and diagnostic.
4. Provider ID appears only in diagnostics/logs, not public strategy DSL.

## 9. M7: Visual Query DSL Subset

### 8.1 Parser

Implement only the subset defined in `05-ConfigurationFormat.md`.

Supported:

```text
//Type
/Type/Type
.
*
@name
@text
@process
@selected
@focused
@disabled
@readonly
@offscreen
@password
@bounds.x/y/width/height
[@attr=value]
contains(@attr,'value')
matches(@attr,'regex')
```

### 8.2 Evaluator

`visual.exists`, `visual.count`, and `visual.match` are the only v1 visual conditions.

### 8.3 Acceptance criteria

1. Invalid visual query is validation error.
2. `visual.exists` works with element type and boolean state.
3. `visual.count` respects min/max.
4. `visual.match` reads an attribute and applies string operators.
5. `@text` is only read when explicitly referenced.
6. Visual query timeout returns `null`.

## 10. M8: Preprocessor Execution Pipeline

### 9.1 Registry

Implement:

```csharp
public interface IStrategyPreprocessorRegistry
{
    bool TryGet(string id, out IStrategyPreprocessor preprocessor);
    IReadOnlyList<IStrategyPreprocessor> GetAll();
}
```

### 9.2 Execution

Before sending a Strategy-backed user message:

1. Build `StrategyExecutionContext`.
2. Run preprocessors in order.
3. Merge variables.
4. Persist `PreprocessorResult`.
5. Render prompt/system prompt with merged variables.
6. Send `UserStrategyChatMessage`.

### 9.3 Compatibility

Keep `{Argument}` alias.

Add path-style variables:

```text
{attachments.selection.text}
{extra.file_manager.selection.items}
{preprocess.file_manager.selection.text}
```

### 9.4 Acceptance criteria

1. Unknown preprocessor prevents execution.
2. Preprocessor failure prevents LLM request.
3. Preprocessor timeout prevents LLM request and shows user-readable error.
4. Variables render into Strategy body.
5. Retry/replay uses persisted `PreprocessorResult` unless explicitly rerun by design.

## 11. M9: UI and Diagnostics Integration

### 10.1 Recommendation UI

Existing Strategy recommendation UI can continue showing:

```text
icon
name
description
priority-sorted order
```

### 10.2 Strategy Details UI

Add or prepare a details view showing:

1. Strategy name and source.
2. Whether it is builtin/user/workspace.
3. Conditions summary.
4. Context it may read.
5. Extra providers inferred.
6. Preprocessors.
7. Enabled/disabled tools.
8. Permissions using `IDynamicLocaleKey`.
9. Recent diagnostics.

### 10.3 Toasts

Show ordinary-user toast when strategies are slow/skipped:

```text
Some strategies took too long to check and were skipped.
```

Rate-limit toasts.

### 10.4 Acceptance criteria

1. Slow matching produces toast.
2. Detailed diagnostics identify slow Strategy and condition/provider.
3. Invalid user Strategy is visible in diagnostics but skipped from recommendation UI.

## 12. M10: Migration Cleanup

After new pipeline works:

1. Move builtin Strategies to runtime `Strategy` construction, `StrategyDefinitionV1`, or embedded `.strategy.md`.
2. Keep legacy `Strategy` only if it remains useful for serialization.
3. Update docs if actual type names differ.
4. Add migration notes for existing stored `UserStrategyChatMessage`.

## 13. Suggested Test Matrix

### 13.1 Parser tests

| Test | Expected |
| --- | --- |
| Minimal valid Strategy | Parses. |
| Missing frontmatter | Parses as markdown only when used via `from` kind markdown/skill. |
| Invalid YAML | Diagnostic. |
| Invalid duration | Diagnostic. |
| Invalid tools map | Diagnostic. |
| Body preserved | Body matches expected text. |

### 13.2 `from` tests

| Test | Expected |
| --- | --- |
| Relative `./SKILL.md` | Source body loaded. |
| Current body exists | Source body replaced. |
| Current body absent | Source body inherited. |
| Override name | Current name wins. |
| Nested `from` | Diagnostic. |

### 13.3 Skills tests

| Test | Expected |
| --- | --- |
| Discover valid `SKILL.md` | SkillDescriptor created. |
| Missing explicit metadata | Name inferred from H1 or directory. |
| Enable skill | Appears in prompt index. |
| Disable skill | Omitted from prompt index. |
| Duplicate ID | Diagnostic. |
| `from: skill://{source}.{skill}` | Resolves to skill source. |

### 13.4 Condition tests

| Test | Expected |
| --- | --- |
| Missing path | `null`. |
| Root null | Strategy hidden. |
| `all` false/null | false if any false, otherwise null. |
| `any` true/null | true if any true. |
| `none` over null | null. |
| Regex timeout | null diagnostic. |
| Array any extension | true for matching item. |

### 13.5 ExtraContext tests

| Test | Expected |
| --- | --- |
| Referenced `extra.file_manager` | Provider inferred. |
| Provider unavailable | path resolves null. |
| Provider timeout | null diagnostic. |
| Virtual item | kind virtual, path null. |

### 13.6 Visual query tests

| Test | Expected |
| --- | --- |
| `//Button` | Finds descendants. |
| `@name` match | Reads attribute. |
| `@text` not referenced | Does not call `GetText`. |
| Invalid query | Validation diagnostic. |
| Query timeout | null diagnostic. |

### 13.7 Preprocessor tests

| Test | Expected |
| --- | --- |
| Declared order | Later variable overrides earlier. |
| Unknown ID | Validation/execution failure. |
| Timeout | Execution stopped. |
| Variable interpolation | Rendered body contains value. |
| Retry | Uses persisted preprocessor result. |

### 13.8 Integration tests

| Test | Expected |
| --- | --- |
| Builtin Strategies still show | No regression. |
| User `.strategy.md` shows when condition true | Recommendation appears. |
| User `.strategy.md` hidden when root null | No recommendation. |
| Strategy ToolRulesets applied | Chat plugins filtered. |
| Invalid user Strategy | App does not crash. |

## 14. Backward Compatibility Rules

1. Existing builtin Strategy IDs should remain stable unless intentionally migrated.
2. Existing `ToolRulesets` behavior must remain.
3. Existing `{Argument}` must remain.
4. Existing stored chat messages must still deserialize.
5. Existing UI should still function even before Strategy details UI is complete.

## 15. Implementation Notes for Agents

When implementing this spec:

1. Start by reading current `src/Everywhere.Core/StrategyEngine`.
2. Preserve current behavior before adding new behavior.
3. Add tests near existing test projects; do not rely only on manual UI testing.
4. Keep user-authored files non-executable.
5. Keep platform-specific file manager code behind `IExtraContextProvider`.
6. Treat timeout and null semantics carefully; many bugs will be false/null confusion.
7. Avoid adding complete XPath. Implement only the documented subset.
8. Do not introduce builtin override semantics in v1.
9. Do not auto-register every `SKILL.md`; only explicit `.strategy.md` or imported managed skill references.
10. Update this documentation if implementation names or defaults intentionally change.

## 16. Definition of Done for v1

v1 is complete when:

1. User `.strategy.md` files can be loaded.
2. SharpYaml parses `StrategyDefinitionV1`.
3. Hand-written normalizer produces runtime `Strategy`.
4. `from` can reference local `SKILL.md`.
5. Skills are discovered, user-manageable, and injected as an enabled index.
6. `from: skill://{source}.{skill}` resolves through the Skills registry.
7. Conditions support structured YAML, path operators, `all/any/none`, and `bool?`.
8. At least one ExtraContext provider works or is stubbed with tests.
9. `extra.file_manager.*` schema is implemented on Windows or clearly marked unsupported with null diagnostics.
10. Visual query subset is implemented and tested.
11. Preprocessors execute before Strategy prompt rendering.
12. Path-style variables render in Strategy body.
13. Existing `ToolRulesets` remains applied.
14. Slow matching produces a Toast and diagnostics.
15. Invalid user Strategies do not crash Everywhere.
16. Existing builtin Strategies and existing chat send flow still work.
