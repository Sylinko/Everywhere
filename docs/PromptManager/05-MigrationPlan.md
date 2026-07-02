# Migration Plan

## 1. Goal

Migration should preserve assistant behavior while moving prompt text into first-class prompt resources.

Users who upgrade should not lose custom assistant prompts. Duplicate prompt bodies should be deduplicated where safe.

## 2. Preconditions

SettingsEngine is assumed to own settings load/save and to run in the `AsyncInitializerIndex.Settings` phase.

Prompt Manager migration is a post-settings migration because prompt text moves from `settings.json` into `prompt.db`.

## 3. Prompt Extraction

Current custom assistants may store system prompt content directly.

Migration should:

1. read existing assistant prompt strings
2. normalize line endings for comparison
3. deduplicate equal prompt bodies by normalized hash
4. map content equal to the built-in default prompt to `Guid.Empty`
5. create prompt resources for unique non-default prompt contents
6. set each assistant prompt reference to the created prompt ID
7. remove the old direct prompt field from the active model

Deduplication should be content-based, not name-based.

## 4. Prompt Names

Generated prompt names should be understandable.

Suggested sources:

1. assistant name
2. first meaningful line of prompt
3. stable fallback such as `Migrated prompt`

If multiple assistants share one migrated prompt, prefer a neutral name rather than one assistant's exact name unless the relationship is obvious.

## 5. Default Prompt

If an existing assistant uses the old default prompt content, migration should reference `Guid.Empty` and should not create a user prompt.

The built-in default prompt remains virtual.

## 6. Existing Empty Prompt

If an assistant has no custom prompt:

1. use `Guid.Empty`
2. do not create a user prompt

The product should avoid creating empty user prompt resources.

## 7. Migration Lifecycle

Migration has two parts:

1. SettingsEngine runs normal versioned settings migrations and patches the singleton `Settings` object.
2. Prompt Manager runs a `Settings + 1` initializer that imports old `SystemPrompt` strings into `prompt.db`, updates `SystemPromptId`, and then removes the old JSON field.

The old direct prompt field should remain available in the JSON document until the Prompt Manager importer has successfully committed the database changes and prompt references. Do not remove it in an earlier settings-only migration.

## 8. Assistant Model Update

After migration:

```text
assistant.SystemPromptId -> prompt id
assistant.SystemPromptId == Guid.Empty -> default prompt
```

The old text field should not remain as an active runtime source.

Temporary migration-only compatibility fields are acceptable.

## 9. Reverse References

Migration should initialize enough data for Prompt Manager to answer:

1. which assistants reference each prompt
2. which prompts are unreferenced
3. whether any assistant has an unresolved prompt reference

## 10. Diagnostics

Migration diagnostics should report:

1. prompt extraction failures
2. invalid assistant IDs
3. unresolved prompt references
4. duplicate collapse events
5. fallback to default prompt

These diagnostics can be shown in logs first and later surfaced in UI.

## 11. Tests

Required tests:

1. assistant prompt string migrates into prompt resource
2. duplicate prompt strings create one prompt resource
3. default prompt content maps to `Guid.Empty`
4. empty prompt does not create a user prompt
5. assistant references migrated prompt ID
6. migration can run on already-migrated settings without duplicating prompts
7. invalid prompt reference shows unresolved diagnostics
