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
| `enabled` | bool | No | true | Whether this Strategy participates in matching. |
| `name` | string | Yes after normalization | source/provider-derived | Display name. |
| `description` | string | No | null | Tooltip/subtitle. |
| `icon` | string | No | null | Icon identifier, preferably Lucide icon name. |
| `priority` | int | No | 0 | Higher appears earlier. |
| `when` | condition object | No | true | Recommendation condition. |
| `tools` | map string -> map string -> bool | No | null | ToolPatternRulesets override for this Strategy. |
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
from: skill://everywhere.my-writing-style
```

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

## 5. Enabled

```yaml
enabled: true
```

Disabled Strategies are loaded for editing/diagnostics but skipped during matching.

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

If absent:

```yaml
when: true
```

Explicit true/false:

```yaml
when: true
```

```yaml
when: false
```

Composite conditions:

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

Supported composite operators:

```text
all
any
none
```

All conditions evaluate to `bool?`, and only root `true` recommends the Strategy.

## 8. Path Conditions

A path condition maps a context path to an operator object.

```yaml
when:
  all:
    - extra.file_manager.selection.items:
        count:
          min: 1
```

Supported root paths:

```text
attachments
visual
clipboard
assistant
environment
extra
```

## 9. String Operators

Examples:

```yaml
environment.os:
  equals: "windows"
```

```yaml
extra.browser.active_tab.url:
  startsWith: "https://arxiv.org/"
```

```yaml
attachments.selection.text:
  regex: "\\bTODO\\b"
```

Full set:

```yaml
equals: "value"
in: ["a", "b"]
contains: "value"
startsWith: "prefix"
endsWith: "suffix"
regex: "pattern"
glob: "*.pdf"
caseSensitive: true
```

Rules:

1. Default string comparison is case-insensitive.
2. `caseSensitive: true` makes supported string operations case-sensitive.
3. Regex uses `options.regexTimeout`.
4. Regex timeout evaluates to `null`.

## 10. Length, Count, Numeric Operators

String length:

```yaml
attachments.selection.text:
  length:
    min: 1
    max: 5000
```

Array count:

```yaml
extra.file_manager.selection.items:
  count:
    min: 1
    max: 20
```

Numeric:

```yaml
extra.file_manager.selection.items:
  any:
    size:
      max: 10485760
```

Supported numeric shape:

```yaml
min: 0
max: 100
equals: 5
```

## 11. Array Operators

Array `any`:

```yaml
extra.file_manager.selection.items:
  any:
    extension:
      in: [".pdf", ".docx", ".txt"]
```

Array `all`:

```yaml
extra.file_manager.selection.items:
  all:
    kind:
      equals: "file"
```

Array `none`:

```yaml
extra.file_manager.selection.items:
  none:
    extension:
      in: [".exe", ".bat", ".cmd", ".ps1"]
```

Inside array operators, paths are relative to each item.

## 12. Attachments Paths

Recommended v1 paths:

```text
attachments.files
attachments.files[].path
attachments.files[].mimeType
attachments.files[].extension
attachments.selection.text
attachments.text
attachments.visual.primary
attachments.visual.items
```

Examples:

```yaml
attachments.files:
  count:
    min: 1
```

```yaml
attachments.files:
  any:
    extension:
      equals: ".png"
```

## 13. Clipboard Paths

Recommended v1 paths:

```text
clipboard.text
clipboard.hasText
clipboard.hasImage
clipboard.files
```

Example:

```yaml
clipboard.text:
  length:
    min: 1
```

Clipboard paths can be read before matching. The Strategy details UI should disclose clipboard access.

## 14. Assistant Paths

Recommended v1 paths:

```text
assistant.id
assistant.name
assistant.model.id
assistant.model.modalities
assistant.model.supportsToolCall
```

Example:

```yaml
assistant.model.modalities:
  contains: "image"
```

## 15. Environment Paths

Recommended v1 paths:

```text
environment.os
environment.architecture
environment.locale
environment.timeZone
environment.currentDate
```

Example:

```yaml
environment.os:
  in: ["windows", "macos"]
```

## 16. Extra Paths

`extra` is the user-facing namespace for context collected by extra providers.

### 16.1 File Manager

Public schema:

```text
extra.file_manager.current_folder.path
extra.file_manager.current_folder.displayName
extra.file_manager.selection.items
extra.file_manager.selection.items[].path
extra.file_manager.selection.items[].displayName
extra.file_manager.selection.items[].kind
extra.file_manager.selection.items[].extension
extra.file_manager.selection.items[].size
```

Example:

```yaml
extra.file_manager.selection.items:
  any:
    extension:
      in: [".pdf", ".docx"]
```

### 16.2 Browser

Recommended future schema:

```text
extra.browser.active_tab.url
extra.browser.active_tab.title
extra.browser.active_tab.domain
```

Example:

```yaml
extra.browser.active_tab.url:
  startsWith: "https://arxiv.org/"
```

### 16.3 Workspace

Recommended future schema:

```text
extra.workspace.root
extra.workspace.name
extra.workspace.git.branch
extra.workspace.git.hasChanges
```

## 17. Visual Conditions

v1 visual condition types:

```text
visual.exists
visual.count
visual.match
```

### 17.1 `visual.exists`

```yaml
visual.exists:
  query: "//TopLevel//Button[@name='Save']"
```

Returns `true` if at least one element matches.

### 17.2 `visual.count`

```yaml
visual.count:
  query: "//ListViewItem[@selected=true]"
  min: 1
  max: 5
```

Returns `true` if the count is within range.

### 17.3 `visual.match`

```yaml
visual.match:
  query: "//TopLevel/@name"
  contains: "Visual Studio"
```

The query must select attribute values. The rest of the object uses normal string/numeric/bool operators.

## 18. Visual Query Syntax

Supported examples:

```text
//Button
/TopLevel/Panel/Button
.//TextEdit[@focused=true]
//TopLevel/@name
//ListViewItem[@selected=true]
//Button[contains(@name,'Save')]
//Document[matches(@text,'error|warning')]
```

Supported concepts:

| Syntax | Meaning |
| --- | --- |
| `//Type` | Descendant search by visual element type. |
| `/` | Strict parent-child step. |
| `.` | Current primary visual element. |
| `*` | Any element type. |
| `@name` | Name attribute. |
| `@text` | Text content, read only when explicitly requested. |
| `@process` | Owning process name. |
| `@selected` | Selected state. |
| `@focused` | Focused state. |
| `@disabled` | Disabled state. |
| `@readonly` | Read-only state. |
| `@offscreen` | Offscreen state. |
| `@password` | Password state. |
| `@bounds.x` | Bounds X. |
| `@bounds.y` | Bounds Y. |
| `@bounds.width` | Bounds width. |
| `@bounds.height` | Bounds height. |
| `contains(@name,'x')` | String contains. |
| `matches(@text,'regex')` | Regex match. |

Not supported in v1:

1. XPath axes.
2. Namespaces.
3. Arbitrary functions.
4. Arithmetic expressions.
5. Index selectors such as `//Button[3]`.
6. Platform-specific fields such as AutomationId/ClassName.

## 19. Tools

`tools` uses the nested `ToolPatternRulesets` format.

```yaml
tools:
  builtin.web:
    "*": true
    web_search: false
  builtin.file_system:
    read_file: true
```

Outer keys are plugin-key glob patterns. Inner keys are function-name glob patterns, and inner values are booleans.

## 20. Preprocessors

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

## 21. System Prompt

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

## 22. Options

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

## 23. Prompt Body

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

## 24. Complete Examples

### 24.1 File Manager Selection

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

### 24.2 Browser URL Strategy

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

### 24.3 Skill-derived Strategy

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

### 24.4 Visual Query Strategy

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

## 25. Validation Checklist

An implementation must validate:

1. Frontmatter exists or file is a valid imported markdown source.
2. YAML parses.
3. `schema` is supported.
4. `id` is valid or derivable.
5. User ID does not use `builtin.`.
6. `from` has one source and no unsupported nested inheritance.
7. `enabled` is bool.
8. `priority` is int.
9. `when` uses supported structure.
10. Visual query syntax is valid.
11. `tools` is map string -> bool.
12. `preprocessors` is string array and IDs exist when registry is known.
13. `options` durations are valid.
14. Body is valid UTF-8/UTF-16 text depending on file encoding support.
