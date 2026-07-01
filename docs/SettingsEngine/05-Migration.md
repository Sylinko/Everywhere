# Migration

## 1. Purpose

The first SettingsEngine migration is a narrow cleanup, not a legacy string-to-typed JSON conversion.

Existing `settings.json` files already store typed JSON values. The migration should only remove shapes that are known to be obsolete or unnecessarily verbose before the SettingsEngine binder reads the document.

## 2. Version Gate

Use the existing settings version gate:

```text
Settings.Version
```

Do not introduce a separate migration ledger for this cleanup. The migration must remain idempotent and should skip safely once the target version has been reached.

## 3. Cleanup Scope

The cleanup currently targets `Model.CustomAssistants[]` only.

Rules:

1. Convert `Icon.Foreground` and `Icon.Background` from ARGB objects such as `{ "A": 140, "R": 179, "G": 47, "B": 115 }` to the current `SerializableColor` JSON string format.
2. Preserve icon colors that are already strings or `null`.
3. Leave malformed color objects unchanged so validation can fail fast and preserve the original file.
4. Remove obsolete assistant root fields: `Temperature`, `TopP`, `ReasoningEffort`, `ThinkingType`, `SupportsTemperature`, and `SupportsTopP`.
5. Do not create or backfill provider option sections such as `OpenAIOptions`, `OpenAIResponsesOptions`, `GoogleOptions`, or `AnthropicOptions`.

## 4. Commit Semantics

Keep the existing `SettingsMigrator` behavior:

1. parse the settings file as a `JsonObject`
2. create a timestamped backup before migration
3. apply pending migrations in memory
4. validate the migrated JSON with SettingsEngine before writing
5. write atomically only after validation succeeds
6. preserve the original file and throw on parse, validation, backup, or write failure

## 5. Tests

Required coverage:

1. ARGB icon colors are converted to `#AARRGGBB` strings.
2. string and `null` icon colors are unchanged.
3. obsolete assistant root fields are removed.
4. provider option sections are not created or overwritten.
5. migration is stable after the target version is reached.
6. malformed color objects fail validation and preserve the original file plus backup.
7. successful migration output can be loaded by SettingsEngine without warning or error diagnostics.
