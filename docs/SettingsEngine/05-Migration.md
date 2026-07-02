# Migration

## 1. Purpose

The first SettingsEngine migration is a narrow cleanup, not a legacy string-to-typed JSON conversion.

Existing `settings.json` files already store typed JSON values. The migration should only remove shapes that are known to be obsolete or unnecessarily verbose before the SettingsEngine binder reads the document.

Settings migrations run during `SettingsEngine.InitializeAsync()`, not during service registration.

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

Keep the `SettingsMigrator` behavior, but invoke it from the settings initializer:

1. parse the settings file as a `JsonObject`
2. create a timestamped backup before migration
3. apply pending migrations in memory
4. validate the migrated JSON with SettingsEngine before writing
5. write atomically only after validation succeeds
6. preserve the original file and throw on parse, validation, backup, or write failure

## 5. Cross-resource Migrations

Some migrations need another local store, such as Prompt Manager moving old assistant prompt text from `settings.json` into `prompt.db`. Those migrations should not run inside `AddSettings()` and should not be forced into a settings-only migration.

Use this lifecycle instead:

1. SettingsEngine migrates and patches settings at `AsyncInitializerIndex.Settings`.
2. The feature owns a later initializer, usually `Settings + 1`, that reads the patched settings and any required preserved JSON fields.
3. The feature commits its own database changes.
4. Only after the feature commit succeeds, it edits the settings JSON through the internal document-editing infrastructure and flushes the result.

The feature migration must be idempotent independently of `Settings.Version`. Version gating is useful but cannot be the only correctness check when a migration spans settings and a database.

## 6. Tests

Required coverage:

1. ARGB icon colors are converted to `#AARRGGBB` strings.
2. string and `null` icon colors are unchanged.
3. obsolete assistant root fields are removed.
4. provider option sections are not created or overwritten.
5. migration is stable after the target version is reached.
6. malformed color objects fail validation and preserve the original file plus backup.
7. successful migration output can be loaded by SettingsEngine without warning or error diagnostics.
8. settings migration runs during the settings initializer, not during DI registration.
9. a post-settings feature migration can edit preserved JSON fields without racing the settings save loop.
