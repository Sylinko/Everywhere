# Strategy Engine Spec: Configuration Format

## 1. File Type

Strategy files use Markdown with YAML frontmatter.

Recommended extension:

```text
.strategy.md
```

The body after the second `---` is the Strategy user prompt body.

```markdown
---
schema: everywhere.strategy/v1
id: user.example
name: "Example"
---

Prompt body here.
```

One file defines one Strategy. Multiple Strategies in one file are not supported.

YAML implementation choice:

1. Use SharpYaml for frontmatter serialization/deserialization.
2. Prefer source-generated metadata where practical.
3. Do not support comment-preserving roundtrip in v1.
4. Strategy Editor may rewrite frontmatter into canonical formatting.
5. User-authored comments in frontmatter are not part of the supported file contract.

## 2. Top-level Field Reference

| Field | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `schema` | string | No | `everywhere.strategy/v1` | Strategy schema version. |
| `id` | string | No | provider-derived | Stable Strategy ID. |
| `from` | string or object | No | null | Single source to derive from. |
| `name` | string | Yes after normalization | source/provider-derived | Display name. |
| `description` | string | No | null | Tooltip/subtitle. |
| `icon` | string | No | null | Icon identifier, preferably Lucide icon name. |
| `priority` | int | No | 0 | Higher appears earlier. |
| `when` | condition object | No | true | Recommendation condition. |
| `tools` | map string -> bool | No | null | ToolRulesets override for this Strategy. |
| `preprocessors` | string[] | No | [] | Predefined preprocessors to run at execution. |
| `systemPrompt` | string | No | null | System prompt override for this request. |
| `options` | object | No | defaults | Matching/runtime options. |
| body | markdown | No | null/source body | User prompt body. |

`name` is required after `from` resolution. A raw file may omit `name` if its `from` source provides one.

## 3. `from`

`from` derives the current Strategy from one source.

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

Allowed `kind` values:

```text
auto
skill
strategy
markdown
url
```

Rules:

1. Only one `from` is allowed.
2. `from` is include-like: final output may look merged, but the source reference is preserved.
3. Current frontmatter fields replace source fields.
4. Current body replaces source body if a body section is present.
5. If current body section is absent, source body is inherited.
6. Multiple inheritance is not supported.
7. Nested `from` should be rejected in v1.
8. Diagnostics should retain both current source and included source when possible.

Examples:

```yaml
from: skill://my-writing-style
```

```yaml
from: skill://codex/deepwiki
```

`skill://deepwiki` uses short-name first-wins resolution ordered by `SkillSourceRoot`. `skill://codex/deepwiki` and `skill://codex.deepwiki` are equivalent precise references to the global skill ID `codex.deepwiki`. Strategy inheritance uses the skill `MarkdownBody`; frontmatter is not copied into the body.

```yaml
from:
  source: E:\Everywhere\Strategies\BaseReview.strategy.md
  kind: strategy
```

```yaml
from:
  source: https://example.com/strategies/research.strategy.md
  kind: url
```

URL support is interface-ready. A v1 implementation may reject URL loading unless a network resolver is explicitly enabled.

## 4. IDs

Examples:

```yaml
id: user.file-manager.summarize-selection
```

```yaml
id: workspace.review-current-file
```

Rules:

1. IDs are stable and case-insensitive.
2. `builtin` is a namespace assigned by the builtin provider, not by user-authored files.
3. User files cannot allocate themselves into `builtin.*`.
4. If a user file writes `id: builtin.foo`, prefer rejecting it with a validation diagnostic.
5. If omitted, user provider may derive ID from relative path.
6. Duplicate IDs within one provider namespace are invalid.
7. User Strategies do not override builtin Strategies.

## 5. Provider Enablement

`.strategy.md` does not have an `enabled` field, and runtime `Strategy` does not store enablement. Enable/disable state belongs to UI/software settings owned by the provider. A future user `IStrategyProvider` should read those settings and return only active strategies from `GetStrategies()`, while any editor surface may separately load disabled files for management and diagnostics. If an old draft file contains `enabled`, v1 parsing treats it as ordinary metadata and it must not affect matching.

## 6. Display Fields

```yaml
name: "Summarize selected files"
description: "Summarize files selected in the file manager"
icon: FileText
priority: 80
```

`icon` should initially use names that map to existing app icon support, preferably Lucide icon names. Invalid icons should fall back to a default icon and produce a validation warning.

## 7. `when`

`when` controls whether a Strategy is recommended.

See `06-ConditionDslSpec.md` for the detailed Condition DSL grammar, binding model, operator semantics, diagnostics, and compiler plan. This section keeps only common examples.

If absent, the Strategy has no condition and can be recommended by the provider. Explicit booleans are allowed:

```yaml
when: true
```

```yaml
when: false
```

Text selection:

```yaml
when:
  attachments.selection.text:
    length:
      min: 1
```

Implicit `all` across multiple clauses:

```yaml
when:
  attachments.selection.text:
    contains: "TODO"
  environment.os:
    in: ["windows", "macos", "linux"]
```

Explicit composite:

```yaml
when:
  all:
    - attachments.selection.text:
        length:
          min: 1
    - none:
        - clipboard.text:
            contains: "secret"
```

File attachments:

```yaml
when:
  attachments.files:
    count:
      min: 1
```

```yaml
when:
  attachments.files:
    any:
      extension:
        equals: ".png"
```

Index and range:

```yaml
when:
  attachments.files[0].path:
    glob: "*.md"
```

```yaml
when:
  attachments.files[^1].extension:
    equals: ".pdf"
```

Extra context:

```yaml
when:
  extra.file_manager.selection.items:
    any:
      extension:
        in: [".pdf", ".docx"]
```

Visual query:

```yaml
when:
  visual.exists:
    query: "//TopLevel//Button[@name='Save']"
```

All conditions evaluate to `bool?`, and only root `true` recommends the Strategy.

## 8. Tools

`tools` uses the existing `ToolRulesets` format.

```yaml
tools:
  builtin.web.*: true
  builtin.web.web_search: false
  builtin.filesystem.read_file: true
```

Keys are plugin or plugin-function glob patterns. Values are booleans.

## 9. Preprocessors

```yaml
preprocessors:
  - selected-text
  - file-manager-selection
```

Rules:

1. IDs must refer to registered preprocessors.
2. Preprocessors run in the declared order.
3. Unknown IDs are validation errors.
4. v1 preprocessors return variables only.
5. Prompt variables use path-style names.

## 10. System Prompt

Short form:

```yaml
systemPrompt: "You are an expert translator."
```

Block form:

```yaml
systemPrompt: |
  You are an expert translator.
  Preserve formatting when possible.
```

It supports the same variable interpolation as body.

## 11. Options

All runtime options live under `options`.

```yaml
options:
  matchingTimeout: 300ms
  conditionTimeout: 80ms
  regexTimeout: 50ms
  visualQueryTimeout: 120ms
  extraTimeout: 200ms
```

Recommended defaults:

| Field | Default | Meaning |
| --- | --- | --- |
| `matchingTimeout` | `300ms` | Total budget for evaluating one Strategy. |
| `conditionTimeout` | `80ms` | Budget for one condition node. |
| `regexTimeout` | `50ms` | Budget for one regex operation. |
| `visualQueryTimeout` | `120ms` | Budget for one visual query. |
| `extraTimeout` | `200ms` | Budget for one extra provider call. |

Durations support:

```text
ms
s
```

Invalid durations are validation errors.

## 12. Prompt Body

The markdown body is the user prompt template.

```markdown
Please explain the selected text:

{attachments.selection.text}
```

Variable rules:

1. Syntax is `{path.to.value}`.
2. `{Argument}` remains supported for compatibility.
3. New variables should use path style.
4. Missing values render as empty string in user-facing execution.
5. Diagnostics should report missing values by path.

## 13. Complete Examples

### 13.1 File Manager Selection

```markdown
---
schema: everywhere.strategy/v1
id: user.file-manager.summarize-selection
name: "Summarize selected files"
description: "Summarize files selected in the file manager"
icon: FileText
priority: 80

when:
  all:
    - extra.file_manager.selection.items:
        count:
          min: 1
    - extra.file_manager.selection.items:
        any:
          extension:
            in: [".pdf", ".docx", ".txt", ".md"]

tools:
  builtin.filesystem.read_file: true
  builtin.web.*: false

preprocessors:
  - file-manager-selection

options:
  matchingTimeout: 300ms
  conditionTimeout: 80ms
  regexTimeout: 50ms
  visualQueryTimeout: 120ms
  extraTimeout: 200ms
---

Please summarize the selected files:

{extra.file_manager.selection.items}
```

### 13.2 Browser URL Strategy

```markdown
---
schema: everywhere.strategy/v1
id: user.browser.arxiv-summary
name: "Summarize arXiv paper"
description: "Summarize the currently open arXiv paper"
icon: FileText
priority: 90

when:
  all:
    - extra.browser.active_tab.url:
        startsWith: "https://arxiv.org/"

preprocessors:
  - browser-active-page

tools:
  builtin.web.*: true
---

Summarize this arXiv paper:

URL: {extra.browser.active_tab.url}

Readable text:
{preprocess.browser.readable_text}
```

### 13.3 Skill-derived Strategy

```markdown
---
schema: everywhere.strategy/v1
id: user.writing.polite-rewrite
from: ./SKILL.md

name: "Polite rewrite"
description: "Rewrite selected text using my writing rules"
icon: PenLine
priority: 60

when:
  all:
    - attachments.selection.text:
        length:
          min: 1
---

Please rewrite this text politely and concisely:

{attachments.selection.text}
```

Because the body is present, it replaces the body loaded from `SKILL.md`. Other fields from the source are replaced by current fields when present.

### 13.4 Visual Query Strategy

```markdown
---
schema: everywhere.strategy/v1
id: user.ide.explain-focused-editor
name: "Explain focused editor"
description: "Explain text in the focused editor"
icon: MessageSquareCode
priority: 70

when:
  all:
    - visual.exists:
        query: ".//TextEdit[@focused=true]"
    - visual.match:
        query: "//TopLevel/@name"
        contains: "Visual Studio"
---

Explain the current focused editor content:

{attachments.selection.text}
```

## 14. Validation Checklist

An implementation must validate:

1. Frontmatter exists or file is a valid imported markdown source.
2. YAML parses.
3. `schema` is supported.
4. `id` is valid or derivable.
5. User ID does not use `builtin.`.
6. `from` has one source and no unsupported nested inheritance.
7. `priority` is int.
8. `when` uses supported structure.
9. Visual query syntax is valid.
10. `tools` is map string -> bool.
11. `preprocessors` is string array and IDs exist when registry is known.
12. `options` durations are valid.
13. Body is valid UTF-8/UTF-16 text depending on file encoding support.
