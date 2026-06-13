# Strategy Engine Spec: Condition DSL

## 1. Purpose

The Condition DSL is the author-facing language used under `.strategy.md` `when`.

It is a small typed language embedded in YAML. Its compiler must parse YAML, normalize sugar into a canonical form, bind paths against registered .NET models, validate operators by type, and produce an executable condition plan that returns `bool?`.

This document is the canonical specification for M5 and later compiler work. The older examples in `03-MatchingSystem.md` and `05-ConfigurationFormat.md` should be read through this document when there is a conflict.

## 2. Goals

1. Make Strategy matching deterministic, explainable, and fast.
2. Keep authoring syntax close to ordinary YAML.
3. Bind paths to .NET domain models through JSON naming rules.
4. Support a compiler pipeline that can later live outside Everywhere.
5. Produce diagnostics with stable codes, line/column when available, and actionable suggestions.
6. Produce an explain plan similar to a SQL plan or Rust-style compiler output.

## 3. Non-goals

1. No arbitrary code execution.
2. No full LINQ parser in v1.
3. No YAML custom tags as required syntax.
4. No XPath or JSONPath dependency for ordinary Strategy paths.
5. No implicit collection projection such as `files[].path`.
6. No auto-loading of expensive context during parsing. Static analysis only infers requirements.

## 4. Compiler Pipeline

Recommended compiler stages:

```text
YAML value
  -> syntax tree
  -> sugar normalization
  -> path/operator tokenization
  -> type binding
  -> validation
  -> static analysis and normalization optimization
  -> bound condition plan
  -> executable evaluator
  -> explain output
```

The runtime evaluator should not discover path semantics by string switches. It should execute a bound plan produced by the compiler.

Static analysis and optimization may simplify plans and produce diagnostics such as redundant, constant, or contradictory expressions. These diagnostics are advisory unless they expose a structural compile error.

## 5. Evaluation Result

Every condition evaluates to `bool?`.

| Value | Meaning |
| --- | --- |
| `true` | The condition matched. |
| `false` | The condition did not match. |
| `null` | The condition could not be evaluated because data was missing, unavailable, timed out, or unsupported on this platform. |

Root recommendation rule:

```text
recommend Strategy iff root condition == true
```

`false` and `null` both hide a Strategy from normal recommendation UI. Diagnostics and explain output must preserve the difference.

## 6. YAML Shape

`when` may be absent, a bool, or a mapping.

```yaml
when: true
```

```yaml
when: false
```

```yaml
when:
  attachments.selection.text:
    length:
      min: 1
```

Absent `when` means no condition is attached. Runtime behavior is equivalent to always recommended for compatibility with builtin strategies.

YAML rules:

1. The DSL uses YAML 1.2 scalar semantics.
2. Authors should quote strings that may be parsed ambiguously by YAML, such as `yes`, `on`, `001`, dates, and values containing backslashes.
3. The compiler explain output must show the parsed value type, not only the original token text.
4. YAML parsing differences are compiler bugs. The frontmatter parser should normalize values before the Condition DSL compiler receives them.

## 7. Canonical Form

The canonical form is nested YAML:

```yaml
when:
  attachments.files:
    any:
      path:
        glob: "*.md"
```

Dotted form is accepted as sugar:

```yaml
when:
  attachments.files.any.path.glob: "*.md"
```

Both compile to the same bound plan:

```text
attachments.files.any(file => glob(file.path, "*.md"))
```

The compiler should expose both original syntax and normalized syntax in explain output.

## 8. Mapping Semantics

A mapping with multiple clauses is implicit `all`.

```yaml
when:
  attachments.selection.text:
    length:
      min: 1
      max: 5000
    contains: "TODO"
```

Equivalent logical shape:

```text
all(
  length(text) >= 1,
  length(text) <= 5000,
  contains(text, "TODO")
)
```

This applies to:

1. Multiple root clauses under `when`.
2. Multiple operators on a value.
3. Multiple fields inside an element predicate.

Example:

```yaml
when:
  attachments.selection.text:
    length:
      min: 1
  environment.os:
    in: ["windows", "macos"]
```

means both clauses must be true.

The compiler may normalize and optimize implicit `all` expressions. For example:

```yaml
when:
  attachments.files.any.path.glob: "*.md"
  attachments.files.any.path.endsWith: ".md"
```

is a valid merge into one predicate scope:

```text
attachments.files.any(file => glob(file.path, "*.md") && endsWith(file.path, ".md"))
```

Static analysis may emit diagnostics for expressions that are provably redundant, always true, always false, or contradictory. These diagnostics must not change the compiled truth table.

## 9. Index and Range Grammar

Property paths are defined by the binding model. Index and range syntax extends a collection-valued path.

```ebnf
indexOrRange = "[", (index | range), "]";
index        = integer | reverseIndex;
reverseIndex = "^", positiveInteger;
range        = rangeBound?, "..", rangeBound?;
rangeBound   = integer | reverseIndex;
integer      = ["-"], digit, { digit };
positiveInteger = nonZeroDigit, { digit };
```

Examples:

```text
attachments.files[0]
attachments.files[0].path
attachments.files[^1]
attachments.files[-1]
attachments.files[..5]
attachments.files[6..-1]
attachments.files[3..]
```

Index rules:

1. `[0]` selects the first item.
2. `[^1]` selects the last item, following C# index-from-end notation.
3. `[-1]` also selects the last item, as a Python-friendly alias.
4. Negative indexes count from the end: `[-2]` is the second-last item.
5. Out-of-range index returns `null` and emits an info-level diagnostic.

Range rules:

1. `[..5]` returns items before index 5.
2. `[6..-1]` returns from index 6 up to, but not including, the last item.
3. `[3..]` returns from index 3 to the end.
4. Range results are new collection values.
5. Range bounds are clamped only if a future spec explicitly says so. Until then, invalid or out-of-range bounds return an empty collection with an info-level diagnostic.

There is no bare `[]` projection syntax in the canonical DSL. Collection-wide logic should use collection operators such as `any`, `all`, `none`, `count`, `contains`, `containsAny`, and `containsAll`.

## 10. Binding Model

Paths bind directly to existing domain models.

Binding rules:

1. The root segment is resolved by a registered root provider.
2. Property segments bind to public .NET properties.
3. `JsonPropertyNameAttribute` defines the public DSL name.
4. If `JsonPropertyNameAttribute` is absent, apply the configured JSON naming policy, defaulting to camelCase.
5. Reserved DSL keywords are not exposed as property names. If a .NET model has such a property, the Strategy binding layer ignores it.
6. The compiler should cache accessors by `(Type, publicName)`.
7. The accessor provider must be replaceable by a future source-generated implementation.

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

Domain models should avoid these public DSL names. If a reserved property is important to expose, the model must give it a different `JsonPropertyName`.

`FileAttachment`-like models should expose both `name` and `path`. Authors can then choose filename-only matching or full-path matching explicitly.

Recommended abstraction:

```csharp
public interface IStrategyPathAccessorProvider
{
    bool TryGetAccessor(Type type, string publicName, out StrategyPathAccessor accessor);
}
```

Initial implementation may use reflection with caching. Future implementations may use `JsonTypeInfo` or a custom source generator.

## 11. Root Providers

The compiler must not hard-code all path roots.

Recommended root binding abstraction:

```csharp
public interface IStrategyPathRootProvider
{
    string RootName { get; }
    Type ValueType { get; }
    object? GetValue(StrategyContext context);
}
```

Examples:

| Root | Example type | Notes |
| --- | --- | --- |
| `attachments` | `IReadOnlyList<ChatAttachment>` or a domain wrapper | Cheap, available today. |
| `activeProcess` | `ProcessInfo?` | Cheap when visual attachments expose process ID. |
| `environment` | environment model | Cheap and always available. |
| `clipboard` | clipboard context model | Requires explicit dependency analysis. |
| `assistant` | selected assistant/model model | Requires app state. |
| `extra` | extra context root model | Requires M6 providers. |
| `visual` | visual query context | Requires visual query milestone. |

Root resolution rules:

1. A root must be registered by a root provider or declared by the compiler host as a deferred external root.
2. Unknown ordinary roots are compile errors. The compiler should suggest near matches when possible.
3. Deferred roots may bind successfully even when no runtime provider is available.
4. If a deferred root has no runtime provider, evaluation returns `null` and emits `condition.root_unavailable`.
5. `extra` is the preferred namespace for user- or integration-provided deferred context.

## 12. Type System

The DSL has these value categories:

```text
null
bool
string
number
object
collection<T>
unknown
```

Binding should infer the static type when possible. Runtime may still return `null`.

Rules:

1. Operators are selected by the current bound value type.
2. Static type mismatches are compile errors. For example, applying `startsWith` to a collection cannot produce an executable condition plan.
3. Runtime type mismatches are warnings and evaluate to `false` unless the operator explicitly says otherwise.
4. Missing data evaluates to `null` and emits `condition.path_missing` when useful.
5. Deferred providers that are unavailable at runtime evaluate to `null`, not `false`.

## 13. Logical Operators

Logical operators are:

```yaml
all:
  - condition
  - condition
```

```yaml
any:
  - condition
  - condition
```

```yaml
none:
  - condition
  - condition
```

```yaml
not:
  equals: "linux"
```

Three-valued rules:

`all`:

```text
false beats null
all true -> true
otherwise -> null
```

`any`:

```text
true beats null
all false -> false
otherwise -> null
```

`none`:

```text
not(any(children)), preserving null
```

`not`:

```text
true -> false
false -> true
null -> null
```

Runtime evaluation may short-circuit. This is preferred for expensive or sensitive providers such as clipboard, visual, or extra context. Static analysis should still list potential requirements and may warn about conditions that are provably constant before runtime.

Do not add reverse operator variants such as `notEquals` or `notIn`. Use `not`.

Dotted sugar:

```yaml
environment.os.not.equals: "linux"
```

Canonical form:

```yaml
environment.os:
  not:
    equals: "linux"
```

YAML custom tag syntax such as `!equals` is not part of this DSL.

## 14. Equality Operators

Supported for scalar values:

```yaml
equals: "windows"
```

```yaml
in: ["windows", "macos"]
```

Negation:

```yaml
not:
  equals: "linux"
```

```yaml
not:
  in: ["linux"]
```

Rules:

1. `equals` operand must be assignable to the current value type.
2. `in` operand must be a collection whose element type is compatible with the current value type.
3. String equality is case-insensitive by default unless `caseSensitive: true` is present in the same operator scope.

## 15. String Operators

Supported on `string`:

```yaml
contains: "TODO"
startsWith: "https://"
endsWith: ".md"
regex: "\\bTODO\\b"
glob: "*.md"
caseSensitive: true
length:
  min: 1
  max: 5000
```

Rules:

1. String operations default to `StringComparer.OrdinalIgnoreCase`.
2. `caseSensitive: true` applies to sibling string operators in the same operator map and uses ordinal comparison.
3. `regex` should be validated during compilation when possible.
4. Invalid regex is a compile error when detected before runtime.
5. Regex timeout evaluates to `null` and emits `regex.timeout`.
6. `glob` is shell-like, not gitignore. `*` and `?` are the only required wildcard tokens in v1.
7. `glob` matches the complete string, not a substring.
8. String comparisons are not culture-sensitive and do not apply Unicode normalization in v1.

Path string rules:

1. The compiler does not normalize path literals.
2. At runtime, `\` and `/` in path comparisons are replaced with the current platform separator before path-specific operators run.
3. Path matching defaults to case-insensitive.
4. `name` and `path` should be separate properties on file attachment models. Use `name` for filename-only checks and `path` for full-path checks.

Regex/glob sugar is deferred. Canonical syntax uses explicit `regex` and `glob` keys.

## 16. Number Operators

Supported on numbers:

```yaml
equals: 5
min: 1
max: 100
in: [1, 2, 3]
```

Multiple numeric operators are implicit `all`:

```yaml
some.size:
  min: 1
  max: 100
```

There is no required `range` wrapper.

## 17. Collection Operators

Supported on `collection<T>`:

```yaml
count:
  min: 1
  max: 10
```

```yaml
contains: "image"
```

```yaml
containsAny: ["image", "audio"]
```

```yaml
containsAll: ["text", "image"]
```

```yaml
any:
  extension:
    in: [".md", ".txt"]
```

```yaml
all:
  mimeType:
    startsWith: "text/"
```

```yaml
none:
  extension:
    in: [".exe", ".bat"]
```

Rules:

1. `count` evaluates the number of elements.
2. `contains`, `containsAny`, and `containsAll` only support scalar collection element types.
3. Object collections must use `any`, `all`, or `none` with an element predicate.
4. `contains` operand must be compatible with the element type.
5. `containsAny` operand must be a collection whose element type is compatible with the current collection element type. It returns `true` when at least one operand value is present.
6. `containsAll` operand must be a collection whose element type is compatible with the current collection element type. It returns `true` only when every operand value is present.
7. `any`, `all`, and `none` operands are predicates evaluated in element scope.
8. `any: "literal"` is invalid because `any` expects a predicate, not an element value.
9. Empty current collection behavior:
   - `any(predicate)` -> `false`
   - `all(predicate)` -> `false`
   - `none(predicate)` -> `true`
   - `contains(value)` -> `false`
   - `containsAny(values)` -> `false`
   - `containsAll(values)` -> `false`
10. Empty operand collection behavior:
   - `containsAny([])` -> `false` and may emit a static warning.
   - `containsAll([])` -> `true` and may emit a static warning because the condition is vacuously true.

Use `containsAny` and `containsAll` for multi-value membership. Do not add reversed membership operators; use `not` when the result needs to be inverted.

Example:

```yaml
assistant.model.modalities:
  contains: "image"
```

Valid because `"image"` is an element.

Multi-value examples:

```yaml
assistant.model.modalities:
  containsAny: ["image", "audio"]
```

```yaml
assistant.model.modalities:
  containsAll: ["text", "image"]
```

Invalid:

```yaml
assistant.model.modalities:
  any: "image"
```

Invalid because `attachments.files` is an object collection:

```yaml
attachments.files:
  contains:
    path: "a.md"
```

Valid:

```yaml
assistant.model.modalities:
  any:
    length:
      min: 4
```

Here each element is a string, so `length` applies to the element itself.

## 18. Object Predicate Scope

Inside collection `any/all/none`, the current value is each element.

For object elements:

```yaml
attachments.files:
  any:
    path:
      glob: "*.md"
```

means:

```csharp
attachments.Files.Any(file => Glob(file.Path, "*.md"))
```

For primitive elements:

```yaml
assistant.model.modalities:
  any:
    length:
      min: 4
```

means:

```csharp
modalities.Any(item => item.Length >= 4)
```

Operator keywords are reserved by the binding model. They are not considered object properties in the DSL, even if the underlying .NET object has a public property with that name.

## 19. Index and Range Evaluation

Index syntax:

```yaml
attachments.files[0].path:
  glob: "*.md"
```

Equivalent nested form:

```yaml
attachments.files[0]:
  path:
    glob: "*.md"
```

Rules:

1. Index applies only to collections.
2. Index and range syntax must follow the grammar in section 9.
3. Out-of-range index evaluates to `null` and emits `condition.index_out_of_range`.
4. Invalid or out-of-range range evaluates to an empty collection and emits `condition.range_out_of_bounds`.
5. Index and range do not imply sorting. They use the provider's existing order.

## 20. Dotted Form Normalization

Dotted form expands into nested mapping.

```yaml
attachments.files.any.path.glob: "*.md"
```

desugars to:

```yaml
attachments:
  files:
    any:
      path:
        glob: "*.md"
```

Then the binder uses type context to decide whether each segment is a path segment or operator.

Normalization merge rules:

1. Dotted keys expand into nested mappings.
2. Expanded mappings are merged recursively.
3. Mapping plus mapping is valid and produces one combined mapping.
4. Scalar plus scalar at the same normalized location is a compile error.
5. Scalar plus mapping at the same normalized location is a compile error.
6. The optimizer may later simplify or diagnose the merged expression, but normalization itself only builds the canonical AST.

Valid merge:

```yaml
attachments.files.any.path.glob: "*.md"
attachments:
  files:
    any:
      path:
        endsWith: ".txt"
```

Equivalent logical shape:

```text
attachments.files.any(file => glob(file.path, "*.md") && endsWith(file.path, ".txt"))
```

Invalid collision:

```yaml
environment.os.equals: "windows"
environment:
  os:
    equals: "linux"
```

## 21. Sugar Policy

Canonical syntax must always be accepted.

Sugar may be added only if it can be desugared losslessly to canonical AST.

Not included in v1:

```yaml
attachments.selection.text: r"\\bTODO\\b"
attachments.selection.text: /\\bTODO\\b/
attachments.files.path: "*.md"
```

Reasons:

1. YAML has its own tag and scalar grammar.
2. Regex escaping is already subtle.
3. Sugar should not make compiler diagnostics worse.

Future sugar candidates must specify exact desugaring and escaping rules.

## 22. Diagnostics

Required diagnostic fields:

```text
severity
code
message
source file
line
column
path or operator span
related spans when useful
```

Severity levels:

| Severity | Meaning |
| --- | --- |
| `debug` | Compiler trace or explain-only note. Not shown in normal UI. |
| `info` | Benign runtime absence or soft mismatch, such as index out of range. |
| `warning` | Strategy can still compile/evaluate, but behavior may surprise the author. |
| `error` | Strategy condition cannot compile or is structurally invalid. |

Recommended codes:

| Code | Default severity | Meaning |
| --- | --- | --- |
| `condition.invalid_yaml_shape` | error | YAML shape cannot be parsed as a condition. |
| `condition.unknown_root` | error | Root is neither registered nor declared as deferred external context. |
| `condition.root_unavailable` | info | Deferred root is valid but unavailable at runtime. |
| `condition.path_missing` | info | Runtime value is unavailable. |
| `condition.segment_missing` | error | Bound type has no property for a path segment. |
| `condition.index_out_of_range` | info | Runtime collection index is out of range. |
| `condition.range_out_of_bounds` | info | Runtime collection range cannot be fully satisfied. |
| `condition.collection_required` | error | Operator requires collection value. |
| `condition.scalar_required` | error | Operator requires scalar value. |
| `condition.type_mismatch` | error | Static value and operator operand types are incompatible. |
| `condition.runtime_type_mismatch` | warning | Runtime value type does not match the compiled type expectation. |
| `condition.operator_unknown` | error | Operator is not valid for the current type. |
| `condition.dotted_collision` | error | Dotted and nested forms assign incompatible values to the same normalized location. |
| `condition.constant_expression` | warning | Static analysis proved an expression is always true or always false. |
| `condition.redundant_expression` | info | Static analysis found a redundant condition. |
| `regex.invalid` | error | Regex cannot compile. |
| `regex.timeout` | warning | Regex evaluation timed out. |
| `visual.query_not_implemented` | info | Visual query shape parsed but no evaluator is available. |

Diagnostics should avoid leaking sensitive runtime values. Include paths, types, and summaries, not full clipboard or file contents.

## 23. Rust-style Error Example

Example invalid condition:

```yaml
when:
  attachments.files:
    any: ".md"
```

Diagnostic:

```text
error[condition.type_mismatch]: collection operator 'any' expects a predicate mapping
 --> polite.strategy.md:4:10
  |
4 |     any: ".md"
  |          ^^^^^ expected mapping such as { extension: { equals: ".md" } }
  |
help: use 'contains' when comparing an element directly
  |
4 |     contains: ".md"
```

## 24. Explain Output

The compiler should expose an explain plan.

Text example:

```text
Condition plan
  all
  ├─ path attachments.selection.text : string?
  │  └─ length
  │     └─ min 1
  └─ path environment.os : string
     └─ in ["windows", "macos"]

Requirements
  attachments.selection.text
  environment.os

Null behavior
  attachments.selection.text missing -> null -> root hidden
```

JSON example:

```json
{
  "kind": "all",
  "children": [
    {
      "kind": "path",
      "path": "attachments.selection.text",
      "valueType": "string",
      "operators": [
        { "kind": "length", "min": 1 }
      ]
    }
  ],
  "requirements": ["attachments.selection.text"],
  "diagnostics": []
}
```

## 25. Examples

### 25.1 Text Selection Exists

```yaml
when:
  attachments.selection.text:
    length:
      min: 1
```

### 25.2 Text Contains TODO

```yaml
when:
  attachments.selection.text:
    contains: "TODO"
```

### 25.3 Text Contains TODO, Case-sensitive

```yaml
when:
  attachments.selection.text:
    caseSensitive: true
    contains: "TODO"
```

### 25.4 Text Regex

```yaml
when:
  attachments.selection.text:
    regex: "\\bTODO\\b"
```

### 25.5 Active Process

```yaml
when:
  activeProcess.name:
    in: ["code", "devenv"]
```

### 25.6 Environment

```yaml
when:
  environment.os:
    equals: "windows"
```

### 25.7 Not Linux

```yaml
when:
  environment.os:
    not:
      equals: "linux"
```

### 25.8 At Least One File

```yaml
when:
  attachments.files:
    count:
      min: 1
```

### 25.9 File Extension Any

```yaml
when:
  attachments.files:
    any:
      extension:
        in: [".md", ".txt"]
```

### 25.10 File Extension None

```yaml
when:
  attachments.files:
    none:
      extension:
        in: [".exe", ".bat", ".cmd", ".ps1"]
```

### 25.11 All Files Are Text

```yaml
when:
  attachments.files:
    all:
      mimeType:
        startsWith: "text/"
```

### 25.12 First File Path

```yaml
when:
  attachments.files[0].path:
    glob: "*.md"
```

### 25.13 Modalities Contains Image

```yaml
when:
  assistant.model.modalities:
    contains: "image"
```

### 25.14 Modalities Contains Any

```yaml
when:
  assistant.model.modalities:
    containsAny: ["image", "audio"]
```

### 25.15 Modalities Contains All

```yaml
when:
  assistant.model.modalities:
    containsAll: ["text", "image"]
```

### 25.16 Primitive Collection Any

```yaml
when:
  assistant.model.modalities:
    any:
      length:
        min: 4
```

### 25.17 Extra File Manager Selection

```yaml
when:
  extra.file_manager.selection.items:
    count:
      min: 1
```

### 25.18 Extra File Manager Any Markdown

```yaml
when:
  extra.file_manager.selection.items:
    any:
      extension:
        equals: ".md"
```

### 25.19 Composite All

```yaml
when:
  all:
    - attachments.selection.text:
        length:
          min: 1
    - environment.os:
        in: ["windows", "macos", "linux"]
```

### 25.20 Implicit All

```yaml
when:
  attachments.selection.text:
    length:
      min: 1
  environment.os:
    in: ["windows", "macos", "linux"]
```

### 25.21 Composite Any

```yaml
when:
  any:
    - attachments.selection.text:
        length:
          min: 1
    - attachments.files:
        count:
          min: 1
```

### 25.22 None Preserves Null

```yaml
when:
  none:
    - extra.file_manager.selection.items:
        count:
          min: 1
```

If `extra.file_manager` is unavailable, this evaluates to `null`, not `true`.

### 25.23 Dotted Equivalent

```yaml
when:
  attachments.files.any.path.glob: "*.md"
```

Equivalent canonical form:

```yaml
when:
  attachments.files:
    any:
      path:
        glob: "*.md"
```

## 26. External Compiler

A future external compiler should support:

```text
everywhere-strategy check ./foo.strategy.md
everywhere-strategy explain ./foo.strategy.md
everywhere-strategy explain ./foo.strategy.md --json
```

`check` should:

1. Parse frontmatter and body.
2. Compile `when`.
3. Validate regex/glob syntax.
4. Bind paths against the selected schema version.
5. Report line/column diagnostics.

`explain` should:

1. Print normalized canonical YAML.
2. Print syntax AST.
3. Print bound AST with types.
4. Print inferred context requirements.
5. Print null propagation behavior.
6. Print warnings for expensive or deferred providers.

## 27. Implementation Milestones

Recommended implementation sequence:

1. Tokenizer for dotted paths and index syntax.
2. YAML normalizer that merges dotted and nested forms or reports collisions.
3. Reflection cached accessor provider based on `JsonPropertyName`, camelCase fallback, and reserved keyword filtering.
4. Root provider abstraction and first registered roots.
5. Syntax AST and bound AST.
6. Type-driven operator binder.
7. Static analysis for constant, redundant, and contradictory expressions.
8. Three-valued short-circuit evaluator from bound AST.
9. Diagnostics with spans.
10. Explain plan text output.
11. Optional `JsonTypeInfo` or source-generated accessor provider.

## 28. Open Questions

1. Should DSL property binding be case-sensitive or case-insensitive while still emitting canonical camelCase suggestions?
2. How should numeric conversions handle decimal vs integer precision?
3. Should `all` over an empty collection be `false` or mathematical `true`? This spec currently chooses `false` for user-facing usefulness.
4. How much visual query syntax belongs in this DSL versus a separate visual sublanguage?
