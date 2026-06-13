# Strategy Engine Spec: Implementation Plan

## 1. Purpose

This document describes how to implement the Strategy Engine incrementally without losing the compiler-oriented design in `06-ConditionDslSpec.md`.

The implementation must preserve existing builtin Strategy behavior while adding user-authored `.strategy.md` files, `from` inheritance, skill references, Condition DSL compilation, diagnostics, and execution hooks.

This plan is intentionally architecture-first. Platform integrations, visual providers, and file-manager providers must plug into registered abstractions instead of being encoded as path string switches.

## 2. Design Principles

1. Treat `.strategy.md` as source code, not a bag of settings.
2. Split parsing, normalization, binding, validation, static analysis, and evaluation into explicit stages.
3. Keep authoring models separate from runtime models.
4. Bind paths to .NET domain models through public JSON names.
5. Reserve DSL keywords; do not let model properties named `any`, `count`, or `contains` change language semantics.
6. Prefer registered factories and providers over switch statements.
7. Preserve `false` and `null` as different results even though both hide a Strategy from normal recommendation UI.
8. Make diagnostics stable enough for UI, tests, and a future external compiler.
9. Keep expensive context collection out of parsing and binding.
10. Do not make user-authored Strategy files executable.

## 3. Milestone Overview

```text
M1. Domain model and versioned definitions
M2. Shared Markdown frontmatter parser
M3. Strategy definition normalizer and `from` source resolution
M4. Skill registry integration and `skill://` source resolution
M5. Condition DSL frontend: syntax, normalization, and source spans
M6. Condition DSL binding: roots, accessors, types, and operators
M7. Condition DSL analysis and runtime plan: optimizer, evaluator, explain
M8. Extra context requirement planning and provider pipeline
M9. Visual query sublanguage
M10. Preprocessor execution pipeline
M11. User Strategy provider, UI, and diagnostics integration
M12. Migration cleanup, external compiler shape, and test hardening
```

Each milestone should be independently testable. A milestone may ship behind provider wiring or UI flags if the compiled behavior is not yet exposed to users.

## 4. M1: Domain Model and Versioned Definitions

M1 introduces the data structures needed by later milestones without changing recommendation behavior.

Required concepts:

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

Rules:

1. `StrategyDefinitionV1` is the versioned authoring model parsed from YAML.
2. `Strategy` is the runtime model used by matching, UI, execution, and chat messages.
3. Enablement is not part of `StrategyDefinitionV1` or runtime `Strategy`.
4. Provider-owned user settings decide whether user strategies are enabled.
5. Runtime model changes must preserve existing message serialization compatibility.

Acceptance criteria:

1. Existing builtin Strategies still appear.
2. Existing selected Strategies still persist in chat messages.
3. No user file loading is required.
4. Tests cover defaults and runtime construction.

## 5. M2: Shared Markdown Frontmatter Parser

M2 provides shared infrastructure for `SKILL.md` and `.strategy.md`.

Components:

```text
MarkdownFrontmatterDocument
MarkdownFrontmatterParser
YamlFrontmatterParser
YamlValueReader
```

Rules:

1. Markdown splitting is shared by Skill and Strategy parsing.
2. YAML parsing is shared and returns structured diagnostics.
3. Shared code knows format and scalar conversion only; it does not know Skill or Strategy business rules.
4. Line endings are normalized consistently.
5. Invalid YAML should not crash Skill or Strategy discovery.

Acceptance criteria:

1. Existing Skill parser behavior remains.
2. `.strategy.md` frontmatter and body parse with source diagnostics.
3. Invalid YAML produces diagnostics.
4. Scalar, list, map, and duration reads are tested.

## 6. M3: Strategy Normalizer and `from` Resolution

M3 converts parsed authoring documents into runtime Strategies.

The normalizer is a compiler stage. It should not be implemented as generic object copying.

Responsibilities:

1. Resolve `from`.
2. Apply replace-only overrides.
3. Apply defaults.
4. Assign provider/source identity.
5. Validate IDs and schema.
6. Parse icon and option values.
7. Convert tools into existing `ToolRulesets`.
8. Preserve includes and diagnostics.

`from` rules:

1. Current field replaces source field.
2. Current markdown body replaces source body when present.
3. Missing current body inherits source body.
4. Final runtime `Strategy.Source` points to the current document.
5. Included sources are preserved in `Strategy.Includes`.
6. Nested `from` is rejected in v1.

Resolvers are registered:

```text
relative file
absolute file
managed skill
future URL or package source
```

Acceptance criteria:

1. Strategy can inherit from a local markdown source.
2. Current body replacement and inheritance both work.
3. Override fields win predictably.
4. Nested `from` produces diagnostics.

## 7. M4: Skill Registry Integration and `skill://`

M4 makes Skills available as Strategy sources without turning Skills into Strategies.

Rules:

1. Skills remain independently managed.
2. `skill://id` resolves to the first matching skill by `SkillSourceRoot` order.
3. `skill://source/id` resolves within a source root.
4. `skill://source.id` is equivalent to `skill://source/id`.
5. Source names are compatibility strings, not enums.
6. Explicit Strategy inheritance uses `SkillDescriptor.MarkdownBody`.
7. Skill enablement does not block explicit `from: skill://...` resolution.

Acceptance criteria:

1. Duplicate skill names resolve by first-wins policy.
2. Source-qualified references resolve predictably.
3. Ambiguous short references produce warning diagnostics.
4. Skill body inheritance works with Strategy overrides.

## 8. M5: Condition DSL Frontend

M5 implements the language frontend defined by `06-ConditionDslSpec.md`.

The frontend does not evaluate conditions. It produces a syntax tree with source spans and a normalized canonical form.

Stages:

```text
YAML value
  -> condition syntax tree
  -> dotted path tokenizer
  -> index/range tokenizer
  -> nested canonical form
  -> recursive merge
  -> frontend diagnostics
```

Required behavior:

1. Accept absent `when`, bool `when`, and mapping `when`.
2. Use YAML 1.2 scalar semantics.
3. Preserve source locations where the parser provides them.
4. Expand dotted keys into nested mappings.
5. Merge dotted and nested mappings recursively.
6. Report scalar/scalar and scalar/mapping collisions.
7. Parse index and range syntax such as `[0]`, `[^1]`, `[-1]`, `[..5]`, `[3..]`.
8. Preserve both original and normalized forms for explain output.

Reserved keywords:

```text
all
any
none
not
equals
in
contains
containsAny
containsAll
startsWith
endsWith
regex
glob
caseSensitive
length
min
max
count
```

Acceptance criteria:

1. Dotted and nested forms produce the same canonical syntax tree.
2. Compatible merges compile into implicit `all`.
3. Duplicate assignment collisions produce diagnostics.
4. Index/range tokens are parsed but not yet evaluated.
5. Invalid YAML shape produces source diagnostics.

## 9. M6: Condition DSL Binding

M6 binds the frontend syntax tree to registered .NET domain models.

The binder is the semantic analysis phase. It must not resolve path semantics with string switches.

Core abstractions:

```text
IStrategyPathRootProvider
IStrategyPathAccessorProvider
IStrategyConditionOperatorBinder
IStrategyConditionOperatorFactory
```

Binding rules:

1. Roots come from registered root providers or compiler-host declared deferred roots.
2. Ordinary unknown roots are compile errors with near-match suggestions when possible.
3. Property names use `JsonPropertyNameAttribute` or configured JSON naming policy.
4. Reserved DSL keywords are not bindable property names.
5. Accessors are cached by `(Type, publicName)`.
6. Future source-generated accessors must be able to replace reflection accessors.
7. Operators are selected by the current bound value type.
8. Static type mismatches are compile errors.
9. Runtime provider unavailability remains a runtime `null`, not a compile failure.

Type categories:

```text
null
bool
string
number
object
collection<T>
unknown
```

Operator groups:

```text
logical: all, any, none, not
equality: equals, in
string: contains, startsWith, endsWith, regex, glob, caseSensitive, length
number: equals, min, max, in
collection: count, contains, containsAny, containsAll, any, all, none
index/range: index, reverse index, range
```

Collection membership rules:

1. `contains`, `containsAny`, and `containsAll` support scalar collections only.
2. Object collections use `any`, `all`, or `none` with an element predicate.
3. Empty operand collections may produce static diagnostics.

Acceptance criteria:

1. Bound AST contains static types.
2. Unknown roots and unknown segments are diagnosed distinctly.
3. Reserved keyword properties are ignored by binding.
4. Object collection `contains` is rejected.
5. Static type mismatch is a compile error.
6. Operator factories can be extended without editing the evaluator.

## 10. M7: Condition DSL Analysis and Runtime Plan

M7 lowers the bound AST into an executable plan.

Stages:

```text
bound AST
  -> static analysis
  -> optional simplification
  -> executable condition plan
  -> short-circuit evaluator
  -> explain output
```

Static analysis may diagnose:

1. Always-true or always-false expressions.
2. Redundant expressions.
3. Contradictory constraints.
4. Empty `containsAny` or vacuous `containsAll`.
5. Expensive or deferred context requirements.

Runtime rules:

1. Every condition evaluates to `bool?`.
2. Root `true` recommends a Strategy.
3. Root `false` and root `null` both hide the Strategy.
4. `all`: false beats null; all true means true; otherwise null.
5. `any`: true beats null; all false means false; otherwise null.
6. `none`: `not(any(children))`, preserving null.
7. Runtime evaluator may short-circuit.
8. Runtime type mismatch is a warning and evaluates to `false` unless an operator specifies otherwise.
9. Missing data evaluates to `null`.
10. Regex timeout evaluates to `null`.

Explain output should include:

1. Original syntax summary.
2. Normalized canonical syntax.
3. Bound paths and static types.
4. Inferred context requirements.
5. Static diagnostics.
6. Null propagation behavior.
7. Short-circuit shape where useful.

Acceptance criteria:

1. Missing path returns `null`.
2. Type mismatch diagnostics distinguish static and runtime cases.
3. Regex timeout returns `null`.
4. `none` over unavailable data returns `null`, not `true`.
5. Scalar collection membership works.
6. Object collection predicates work.
7. Explain output is stable enough for snapshot tests.

## 11. M8: Extra Context Requirement Planning and Providers

M8 connects compiled requirements to context collection.

The Condition DSL compiler infers what roots may be needed. Providers decide whether and how to collect those roots at runtime.

Provider rules:

1. Providers are registered by public root.
2. Providers return domain models, not ad-hoc dictionaries, whenever the schema is stable.
3. Provider IDs appear in diagnostics/logs, not in author-facing path syntax.
4. Provider timeouts produce `null` with diagnostics.
5. Unsupported platform returns `null`, not exceptions.
6. Expensive providers are not invoked if short-circuit evaluation has already decided the result.

Acceptance criteria:

1. Compiler can infer deferred roots from the bound plan.
2. Runtime can request only the roots needed by the plan.
3. Provider unavailable and provider timeout are distinguishable diagnostics.
4. At least one provider is implemented or stubbed behind the provider abstraction.

## 12. M9: Visual Query Sublanguage

M9 implements visual queries as a separate sublanguage plugged into the Condition DSL.

Rules:

1. Visual query parsing is not part of ordinary property path parsing.
2. `visual.exists`, `visual.count`, and `visual.match` are Strategy operators over the visual root.
3. The visual query parser produces its own AST and diagnostics.
4. The evaluator reads expensive visual attributes only when the query requests them.
5. Timeout returns `null` with diagnostics.

Acceptance criteria:

1. Invalid visual query is a validation error.
2. Visual query timeout returns `null`.
3. Text/attribute reads are demand-driven.
4. Visual diagnostics preserve query spans when possible.

## 13. M10: Preprocessor Execution Pipeline

M10 executes declared preprocessors before rendering Strategy prompts.

Rules:

1. Preprocessors are registered by ID.
2. Unknown preprocessor IDs are validation errors.
3. Preprocessors run in declared order.
4. Preprocessor variables merge into the prompt rendering scope.
5. Preprocessor failure prevents the LLM request.
6. Retry/replay behavior must be explicit and tested.

Compatibility:

```text
{Argument}
{attachments.selection.text}
{extra.file_manager.selection.items}
{preprocess.some_provider.some_value}
```

Acceptance criteria:

1. Variables render into Strategy body and system prompt.
2. Timeout and failure diagnostics are user-visible.
3. Existing `{Argument}` behavior remains.

## 14. M11: User Strategy Provider, UI, and Diagnostics

M11 exposes user strategies through provider-owned settings and diagnostics.

Rules:

1. User Strategy loading belongs to a provider, not the core registry.
2. Enablement is provider setting state.
3. Disabled user Strategies are filtered before `GetStrategies()` results are returned.
4. Invalid user Strategies appear in diagnostics but not recommendation UI.
5. Details UI should show source, includes, inferred context, preprocessors, tools, and recent diagnostics.

Acceptance criteria:

1. Existing builtin recommendations remain unchanged.
2. User strategies can be enabled/disabled without changing the Strategy model.
3. Diagnostics identify invalid files and skipped strategies.
4. Slow matching can be surfaced without noisy repeated toasts.

## 15. M12: Migration, External Compiler Shape, and Hardening

M12 completes cleanup after the compiled pipeline is working.

Tasks:

1. Remove obsolete path-switch evaluators.
2. Align docs with actual type names.
3. Add migration notes for stored `UserStrategyChatMessage` values.
4. Decide which builtin Strategies, if any, move to embedded `.strategy.md`.
5. Shape a future external compiler command:

```text
everywhere-strategy check ./foo.strategy.md
everywhere-strategy explain ./foo.strategy.md
everywhere-strategy explain ./foo.strategy.md --json
```

Acceptance criteria:

1. Stored chat messages still deserialize.
2. Builtin Strategy IDs remain stable unless intentionally migrated.
3. External compiler output can be derived from existing diagnostics and explain plan APIs.

## 16. Test Matrix

### 16.1 Parser and Normalizer

| Test | Expected |
| --- | --- |
| Minimal valid Strategy | Parses. |
| Invalid YAML | Diagnostic. |
| Invalid duration | Diagnostic. |
| Body preserved | Normalized line endings only. |
| Current body over source body | Current body wins. |
| Missing current body | Source body inherited. |
| Nested `from` | Diagnostic. |

### 16.2 Skill Resolution

| Test | Expected |
| --- | --- |
| `skill://id` duplicate | First by `SkillSourceRoot` order. |
| `skill://source/id` | Source-qualified match. |
| `skill://source.id` | Equivalent to slash form. |
| Missing skill | Diagnostic. |

### 16.3 Condition Frontend

| Test | Expected |
| --- | --- |
| Dotted form | Canonical tree. |
| Nested form | Same canonical tree. |
| Compatible merge | Implicit `all`. |
| Duplicate scalar collision | Diagnostic. |
| Index/range tokens | Parsed with spans. |
| YAML ambiguous scalar | Parsed type shown in explain. |

### 16.4 Binding

| Test | Expected |
| --- | --- |
| JsonPropertyName path | Binds. |
| camelCase fallback | Binds. |
| Reserved keyword property | Ignored. |
| Unknown root | Compile error. |
| Deferred root unavailable | Runtime null. |
| Static type mismatch | Compile error. |
| Object collection `contains` | Compile error. |

### 16.5 Evaluation

| Test | Expected |
| --- | --- |
| Missing path | `null`. |
| `all` false/null | `false`. |
| `any` true/null | `true`. |
| `none` over null | `null`. |
| Regex timeout | `null` diagnostic. |
| Empty `containsAny` operand | `false` plus advisory diagnostic. |
| Empty `containsAll` operand | `true` plus advisory diagnostic. |
| Short-circuit avoids deferred provider | Provider not invoked. |

### 16.6 Extra Context and Visual

| Test | Expected |
| --- | --- |
| Deferred root inferred | Requirement listed. |
| Provider unavailable | `condition.root_unavailable`. |
| Provider timeout | Timeout diagnostic. |
| Visual query invalid | Validation diagnostic. |
| Visual query timeout | `null`. |

### 16.7 Preprocessors and Integration

| Test | Expected |
| --- | --- |
| Declared order | Later variable overrides earlier according to merge rules. |
| Unknown ID | Validation/execution failure. |
| Timeout | Execution stopped. |
| Variable interpolation | Rendered prompt contains value. |
| Builtin Strategies | No regression. |
| Invalid user Strategy | App does not crash. |
| ToolRulesets | Existing behavior remains. |

## 17. Backward Compatibility Rules

1. Existing builtin Strategy IDs should remain stable unless intentionally migrated.
2. Existing `ToolRulesets` behavior must remain.
3. Existing `{Argument}` must remain.
4. Existing stored chat messages must still deserialize.
5. Existing UI should still function before Strategy details UI is complete.

## 18. Definition of Done for v1

v1 is complete when:

1. User `.strategy.md` files can be loaded through a provider.
2. Frontmatter parsing and normalization produce runtime Strategies.
3. `from` supports local markdown and managed skill sources.
4. Skills are discoverable, manageable, and usable as explicit Strategy sources.
5. Condition DSL compiles through frontend, binding, analysis, executable plan, and explain output.
6. Conditions evaluate with `bool?` and short-circuit semantics.
7. Deferred context providers integrate through registered roots.
8. Visual query support is implemented or explicitly diagnosed as unavailable.
9. Preprocessors execute before Strategy prompt rendering.
10. Existing builtin Strategies and chat send flow still work.
11. Diagnostics are suitable for UI and future external compiler output.
