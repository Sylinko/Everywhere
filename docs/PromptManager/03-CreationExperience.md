# Creation Experience

## 1. Goal

Prompt creation should serve two groups:

1. beginners who want useful prompts without learning template syntax
2. advanced users who want direct control over the full template

The create flow should make the guided path feel natural while keeping advanced editing one click away.

## 2. Entry Points

Prompt creation can start from:

1. guided creator
2. blank advanced editor
3. copy from existing prompt
4. migration-created prompt
5. future shared/imported prompt

The primary "New Prompt" action should open the guided creator by default, with an obvious advanced option.

## 3. Guided Creator

Guided creator steps:

1. optionally enter a prompt name
2. choose persona
3. optionally enter how the assistant should address the user
4. choose up to 3 work scenarios
5. choose tone or interaction style
6. choose answer detail level
7. choose organization style
8. enter optional additional requirements
9. review live preview and warnings
10. save as a normal prompt

The flow should not ask users to understand internal template mechanics before they can create a prompt.

Prompt name is optional. If it is empty, display fallback follows the data model rules: guided prompts fall back to persona name, advanced prompts fall back to a trimmed template preview.

## 4. Persona Options

Persona examples:

1. general assistant
2. professional advisor
3. programming assistant
4. writing editor
5. translation assistant
6. research assistant
7. learning mentor
8. product and strategy partner

Each persona contributes deterministic template fragments.

## 5. Work Scenario Options

Work scenarios are multi-select with a maximum of 3 selected items.

Scenario examples:

1. general Q&A
2. programming and development
3. code review
4. writing and editing
5. data analysis
6. learning and education
7. translation and language
8. product planning
9. troubleshooting
10. research and planning

Scenario choice should influence required and recommended placeholders.

There is no primary scenario in v1. The generated prompt should describe the selected scenarios as areas the prompt is optimized for and state that the user's current request wins when scenario guidance conflicts.

## 6. Tone, Detail, and Organization

Tone/style is single-select.

Recommended tone options:

1. professional and rigorous
2. friendly and patient
3. concise and direct
4. Socratic or guiding
5. formal business
6. creative and exploratory

Answer detail level is single-select.

Recommended detail options:

1. concise
2. balanced
3. detailed

Organization style is single-select. It replaces the earlier technical "output format" model.

Recommended organization options:

1. automatic, recommended
2. conclusion first
3. step-by-step
4. key points
5. compare options
6. examples first
7. natural conversation

Strict technical formats such as JSON should not be part of the beginner quick configuration. They belong in advanced editing or a later developer-oriented mode.

## 7. Prompt-Specific User Addressing

The quick creator can include an optional "how to address me" field.

This is prompt-specific metadata and prompt text, not a global user profile. It should be clear in UI copy that the value becomes part of this prompt.

## 8. Placeholder Choices

The guided creator should expose placeholders as understandable toggles, not raw syntax first.

Examples:

| UI Label | Template placeholder |
| --- | --- |
| Current date | `{Date}` |
| Current time | `{Time}` |
| Operating system | `{OS}` |
| System language | `{SystemLanguage}` |
| Working directory | `{WorkingDirectory}` |
| Built-in default prompt | `{DefaultSystemPrompt}` |

Advanced users can edit the raw placeholders later.

`{SkillsPrompt}` should not be presented as a normal beginner toggle. It is owned by `{DefaultSystemPrompt}` in the default composition. Advanced users can still reference `{SkillsPrompt}` directly when intentionally bypassing the default prompt.

## 9. Generated Template

The generated template should be plain template text.

After save, there should be no special "guided prompt" runtime behavior. The prompt is just a prompt.

Optional source metadata can remember that the prompt came from a recipe, but runtime rendering should not depend on recipe data.

Default guided templates should start with `{DefaultSystemPrompt}` and then add the selected persona, prompt-specific user addressing, scenarios, tone, detail level, organization style, and additional requirements. This gives custom prompts a stable foundation and keeps skills/tool instructions centralized.

Example shape:

```text
{DefaultSystemPrompt}

# User Preference
Address the user as: DearVa

# Persona
You are a professional advisor.

# Work Scenarios
This prompt is optimized for:
- General Q&A
- Product planning
- Research and planning

When scenario guidance conflicts, prioritize the user's current request.

# Interaction Style
Be professional and rigorous.

# Detail Level
Use a balanced level of detail.

# Organization
Choose the clearest structure automatically. Use paragraphs, lists, steps, or tables when they improve readability.

# Additional Requirements
...
```

## 10. Advanced Transition

Users can switch to advanced editing at any time.

Recommended behavior:

1. show generated template before save
2. allow direct edits
3. keep diagnostics active
4. store a MessagePack recipe snapshot for guided prompts
5. mark the prompt as detached from the recipe after advanced editing saves over the generated template
6. warn that using quick configuration again will regenerate and overwrite the current template
7. offer "save as new prompt" where practical to avoid destructive overwrite

The product should avoid trapping users in either beginner or advanced mode.

Advanced editing must not try to reverse-engineer arbitrary template text back into quick configuration choices.

## 11. Save Behavior

Saving from guided creator should:

1. create a normal prompt ID
2. persist the optional prompt name
3. persist the final template
4. persist the recipe snapshot as opaque MessagePack metadata
5. show warnings but allow intentional warnings
6. optionally offer to assign the prompt to an assistant
7. return to the Prompt Manager page and select the newly created prompt

Errors such as empty template should block save.

Warnings such as bypassing `{DefaultSystemPrompt}` should not block save.
