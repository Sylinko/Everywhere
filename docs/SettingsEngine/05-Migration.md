# Migration

## 1. Purpose

The first Settings Engine migration converts the legacy `IConfiguration`-maintained `settings.json` into canonical JSON owned by Settings Engine.

This migration should remain within the existing versioned settings migration architecture.

## 2. Migration Architecture

Do not introduce a new migration task ledger.

Use the existing version gate:

```text
Settings.Version
```

The migration itself should be carefully written so that if its task sees already-migrated shape, it skips safely. This is enough for this codebase and keeps migration cost low.

## 3. Algorithm

Recommended algorithm:

```text
1. parse legacy settings.json as JsonObject
2. run previous versioned migrations
3. create pre-migration backup/history snapshot
4. normalize legacy leaf values at hard-coded paths
5. normalize complex object shapes
6. run business migrations, such as prompt extraction
7. write canonical settings.json
8. read canonical JSON with new Settings Engine
9. if validation succeeds, initialize sync baseline later
10. if validation fails, keep original object/file state and report diagnostics
```

The migration should not bind the full legacy file into `Settings` as the source of truth.

## 4. Explicit Defaults

Migration helpers must receive explicit default values.

Do not read defaults dynamically from the current `Settings` model. Defaults may change in future versions, and large-version-span upgrades must remain deterministic.

Example shape:

```csharp
NormalizeValue<T>(
    JsonObject root,
    string path,
    T defaultValue,
    JsonTypeInfo<T> typeInfo,
    Func<string, T?>? legacyParser = null);
```

Rules:

1. If the path does not exist, do not write it unless the migration task requires a new value.
2. If the path exists as a legacy string, parse using the legacy parser.
3. If parsing fails, write the explicit default value.
4. If the path already has canonical shape, validate and rewrite only if needed.
5. If null is valid, preserve null.
6. If null is invalid, write the explicit default value.

## 5. Legacy Converter Compatibility

The migration should locally encode legacy conversion behavior.

Known examples:

1. invalid GUID string maps to `Guid.Empty`
2. invalid enum string maps to an explicit default
3. locale strings map to `LocaleName`
4. color strings map to `SerializableColor`
5. `Customizable<T>` values preserve default/custom value semantics

These helpers can be private nested types or local functions inside the migration class. They should not become runtime Settings Engine API.

After canonical JSON is produced, legacy fallback converters such as `FallbackGuidConverter` and `FallbackEnumConverter` can be removed if no other runtime dependency remains.

## 6. Hard-coded Paths

Use hard-coded paths for this migration.

Settings are still small enough that this is safer than an overly generic migration engine. Hard-coded paths also make each conversion decision explicit.

Examples:

```text
Display.Language
Display.Theme
Display.AccentColor
Model.SelectedCustomAssistantId
Model.CustomAssistants[].Id
Model.CustomAssistants[].Icon
Shortcut.ChatWindow.Main.Key
Shortcut.ChatWindow.Main.Modifiers
```

The actual path list should be produced during implementation by scanning settings-reachable models.

## 7. Business Migration Ordering

Do format normalization before business migration.

Order:

1. normalize legacy value formats
2. normalize complex object shapes
3. migrate assistant `SystemPrompt` strings into prompt resources
4. convert assistants to prompt references
5. repair ApiKey metadata and orphan secret state where possible
6. write canonical settings JSON

This prevents business migration from operating on malformed old values.

## 8. Validation

After writing canonical JSON in memory, validate it before committing.

Validation checks:

1. new Settings Engine can patch a fresh runtime settings object
2. serialized subtree values can be read
3. enum values are valid strings
4. GUID values are canonical strings
5. assistant prompt references resolve to a real prompt or the built-in default prompt
6. write-back produces stable canonical JSON for known fields

If validation fails, do not destroy the pre-migration file. Keep diagnostics.

## 9. ColoredIcon Migration Test

`ColoredIcon` should be used as a test case for serialized subtree and polymorphism.

Goals:

1. migrate old tagged-object JSON into the new polymorphic shape
2. keep a familiar discriminator field, such as `Type`
3. validate `SettingsSerializedSubtree`
4. verify failed deserialization keeps the original runtime object unchanged

## 10. Tests

Required tests:

1. invalid GUID string falls back to explicit default
2. invalid enum string falls back to explicit default
3. nullable value preserves null
4. non-nullable null falls back to explicit default
5. existing canonical JSON remains stable
6. old `Customizable<T>` shape migrates correctly
7. `ColoredIcon` polymorphic subtree migrates and reads
8. unknown keys are preserved
9. strict/pruned sections remove unknown keys
10. validation failure preserves the old file
