# Editor, Preview, and Diagnostics

## 1. Advanced Editor

Advanced mode is a direct prompt template editor.

Requirements:

1. multiline text editing
2. placeholder highlighting
3. placeholder reference list
4. dirty/save state
5. diagnostics shown near the editor
6. reference count visible for existing prompts

AvaloniaEdit is the planned editor for advanced mode.

The editor should only highlight prompt placeholders. It should not render Markdown syntax, headings, lists, or code fences. Prompt templates are plain text with placeholders.

## 2. Layout

Selected-prompt layout:

```text
read mode
  top: diagnostics
  header: prompt name + actions
  body
    left: rendered preview / raw template tabs
    right: assistant references

edit mode
  top: diagnostics
  header: prompt name + save/cancel actions
  body
    left: template editor
    right: rendered preview + diagnostics
```

Read mode and edit mode are distinct. The main page starts in read mode for an existing prompt. The Edit action switches to edit mode; Save or Cancel exits edit mode.

The raw template tab in read mode is read-only but still highlights placeholders. Editing is only available in edit mode.

Advanced edit mode left side:

```text
optional prompt name
AvaloniaEdit template editor
placeholder reference list
```

The left side scrolls as one unit when needed. The placeholder reference list is not collapsible in v1.

## 3. Preview

Preview renders the template content.

Requirements:

1. refresh about once per second
2. render time-sensitive placeholders such as date and time
3. show explanatory values for context that is unavailable in the manager
4. expand the default prompt, including its skill instructions, when available
5. display rendered content, because picker paste also uses rendered content
6. style placeholder-derived values with the same color mapping used by the editor

Preview should use the same renderer semantics as chat execution.

Preview should be represented as inlines rather than a plain string so placeholder-derived values can be styled and updated incrementally.

For expanded placeholders:

1. `{DefaultSystemPrompt}` uses a subdued gray placeholder color.
2. Its rendered default-prompt content can be styled subtly, but should not visually dominate the preview.
3. Nested dynamic placeholders inside the default prompt, such as `{Date}`, `{Time}`, or `{SkillsPrompt}`, keep their own placeholder colors.
4. Short dynamic placeholders show their rendered values in their assigned colors.

## 4. Preview Context

The editor preview can provide:

1. `{Date}`
2. `{Time}`
3. `{OS}`
4. `{SystemLanguage}`
5. `{WorkingDirectory}`
6. `{DefaultSystemPrompt}`
7. `{SkillsPrompt}`

Unavailable variables should be shown with explanatory placeholder output and diagnostics when useful.

`{SkillsPrompt}` is still a valid advanced placeholder, but normal prompts should receive skill instructions through `{DefaultSystemPrompt}`.

## 5. Shared Template Parsing

Advanced editing should not implement separate regexes for editor highlighting, preview, diagnostics, and runtime rendering. These features should share a prompt template parser.

Suggested parser output:

| Token | Meaning |
| --- | --- |
| `Text` | Literal template text. |
| `Placeholder` | Valid placeholder syntax such as `{Date}`. |
| `EscapedBrace` | Literal escaped braces such as `{{` or `}}` if supported. |
| `InvalidPlaceholder` | Malformed placeholder-like syntax. |

Each token should carry source span information.

Consumers:

1. chat/runtime renderer
2. editor colorizer
3. read-only raw template display
4. preview inline renderer
5. diagnostics
6. future completion provider

The current string-only renderer can remain as a compatibility facade, but its implementation should be able to use the shared parser.

## 6. Placeholder Color Mapping

Placeholder colors should be consistent across:

1. AvaloniaEdit template editor
2. read-only raw template tab
3. rendered preview
4. placeholder reference list

Use a fixed high-contrast palette rather than true random colors. Assign colors by placeholder name with stable hashing and collision mitigation within the current document. This gives a rainbow effect while keeping the same placeholder stable between refreshes.

`{DefaultSystemPrompt}` should use a subdued gray color instead of a saturated rainbow color.

Unknown placeholders should still receive a visible style and diagnostic. They may use a warning color, underline, or warning background in addition to their placeholder color.

## 7. AvaloniaEdit Highlighting

AvaloniaEdit should use a custom `DocumentColorizingTransformer` or equivalent lightweight colorizer.

Rules:

1. highlight only placeholder spans
2. do not use TextMate for prompt templates
3. do not highlight Markdown syntax
4. keep colors aligned with `PromptPlaceholderPalette`
5. invalidate/recolor only when the document text or placeholder palette changes

Completion after typing `{` is useful but optional after the first editor implementation.

## 8. Placeholder Reference List

Advanced edit mode should show a non-collapsible placeholder reference list.

Recommended entries:

| Placeholder | Meaning |
| --- | --- |
| `{DefaultSystemPrompt}` | Everywhere default system instructions. |
| `{SkillsPrompt}` | Enabled Skills instructions. |
| `{Date}` | Current date. |
| `{Time}` | Current time. |
| `{OS}` | Operating system. |
| `{SystemLanguage}` | System language. |
| `{WorkingDirectory}` | Working directory. |

Each entry should show the same color marker used by editor and preview. Clicking an entry can insert the placeholder at the editor caret when implementation cost is reasonable.

## 9. Incremental Preview Updates

The preview refreshes about once per second because time-sensitive placeholders can change.

Avoid clearing and rebuilding all inlines on every tick.

Recommended approach:

1. render to a list of preview segments
2. give each segment a stable key, such as source occurrence path plus placeholder name
3. maintain slots for current inlines
4. when keys are unchanged, update `Run.Text`, foreground, and metadata in place
5. when only a small range changes, patch that range
6. fall back to full rebuild when structure changes too much

This mirrors the incremental update strategy already used for terminal code block inlines.

Template edits can be debounced. Timer ticks should mostly update rendered values for placeholders such as `{Time}`.

## 10. Diagnostics

Diagnostics should run in both guided and advanced modes.

Recommended categories:

1. template validity
2. placeholder validity
3. assistant integration warnings
4. migration/referenced prompt warnings
5. future sync/import warnings

The editor should avoid noisy warnings. A warning is useful only when it helps the user improve the prompt or understand behavior.

Diagnostics shown on the Prompt Manager page are not dismissible and are not persisted as user-dismissed state. They should update when the selected prompt, template text, preview context, or references change.

Only an empty template blocks saving in advanced mode. Missing default prompt, missing skills prompt, unknown placeholders, and preview-context warnings are reminders only.

## 11. Default Prompt Warning

Most assistant prompts should include `{DefaultSystemPrompt}` unless they intentionally replace the entire system prompt.

If a prompt does not include `{DefaultSystemPrompt}`, show an informational warning:

```text
This prompt does not include Everywhere's default instructions. It may omit base behavior and skill/tool guidance.
```

This is not an error.

## 12. Skills Prompt Warning

Skill instructions are normally included by `{DefaultSystemPrompt}`.

If a prompt bypasses `{DefaultSystemPrompt}` and does not include `{SkillsPrompt}`, optionally show a secondary warning:

```text
This prompt does not include skill instructions. Skills may not be available unless they are injected elsewhere.
```

This is not an error. Some prompts intentionally avoid skills.

## 13. Unknown Placeholder Warning

Unknown placeholders should be highlighted.

The warning should distinguish:

1. unknown in all known contexts
2. known but unavailable in the preview context
3. future or plugin-provided placeholders

This avoids scaring advanced users who intentionally use context-specific placeholders.

## 14. Prompt Picker

The global prompt picker should:

1. list prompts
2. support search
3. show prompt names and short previews
4. paste rendered content
5. reuse platform text insertion/clipboard work

Picker rendering should use the picker context, not the editor preview context.

## 15. Delete and Reference Warnings

Before deleting a prompt:

1. show whether it is referenced
2. show referencing assistants when practical
3. block deletion of the built-in default prompt
4. allow deleting unreferenced prompts directly
5. require strong confirmation before deleting a referenced prompt
6. reset assistant references to the built-in default prompt after confirmed deletion

Referenced prompt deletion should never leave assistants with broken references.
