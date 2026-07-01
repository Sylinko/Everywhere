# Settings Engine: Overview

## 1. Purpose

Settings Engine is the planned replacement for the current `IConfiguration`-based settings persistence path.

The goal is not to deserialize `settings.json` into a new `Settings` object. The goal is to build a JSON-backed object patch engine that can:

1. keep `settings.json` as the human-readable storage format
2. preserve unknown JSON keys by default
3. patch the existing runtime `Settings` object without breaking MVVM references
4. support source-generated metadata for AOT-friendly binding
5. run small versioned cleanups before binding
6. prepare settings for later semantic sync

## 2. Fixed Decisions

Current fixed decisions:

1. `_jsonObj` is the settings document source of truth.
2. The runtime `Settings` object is patched from `_jsonObj`.
3. `IConfiguration` should be removed from the core settings pipeline.
4. First-stage migration uses hard-coded paths for known cleanup shapes.
5. Existing `settings.json` values are already typed JSON; migration must not assume string-only leaves.
6. Default binding behavior is patching the existing object graph.
7. Collections keep their collection instance, but their items are synchronized to the JSON array.
8. Unknown JSON keys are preserved by default.
9. Pruning unknown keys requires explicit metadata.
10. Whole-subtree `System.Text.Json` handling is opt-in through `SettingsSerializedSubtree`.
11. If serialized-subtree reading fails, keep the existing object unchanged and report a warning.

## 3. Semantic Merge Scope

Semantic merge means cloud-sync conflict resolution using:

```text
base settings
local settings
remote settings
```

It is not the first-stage Settings Engine goal.

The first stage should only reserve descriptor metadata that future semantic merge can use, such as item keys and unknown-member policies. It should not implement cloud-sync three-way merge yet.

## 4. First-stage Goals

First-stage work should deliver:

1. a settings document store backed by `JsonNode`
2. a source generator that emits descriptors from the `Settings` root
3. a patch binder that updates an existing `Settings` object
4. local write-back from settings changes to `_jsonObj`
5. attribute-controlled serialized subtree boundaries
6. attribute-controlled unknown member handling
7. a cleanup migration for known obsolete settings shapes
8. removal of `IConfiguration` from the core settings read/write path

## 5. Non-goals

First-stage work should not include:

1. cloud sync
2. semantic three-way merge
3. prompt manager UI
4. WebDAV/RFC 6578 integration
5. end-to-end encryption
6. a fully generic replacement for every `IConfiguration.Binder` feature
7. comment-preserving JSON text edits

The engine should preserve unknown keys, but comment preservation is a separate capability. `JsonNode` parsing and writing will not preserve comments.

## 6. Relationship to Existing Code

Useful existing code:

| Area | Existing path |
| --- | --- |
| Current settings root | `src/Everywhere.Core/Configuration/Settings.cs` |
| Current DI registration | `src/Everywhere.Core/Configuration/SettingsExtensions.cs` |
| Current auto-save observer | `src/Everywhere.Core/Initialization/SettingsInitializer.cs` |
| Existing settings source generator | `src/Everywhere.Configuration.SourceGenerator` |
| Current settings migrations | `src/Everywhere.Core/Configuration/Migrations` |
| Writable JSON reference | `3rd/WritableJsonConfiguration` |
| Binder reference | `3rd/Microsoft.Extensions.Configuration.Binder` |

## 7. Document Map

| Document | Purpose |
| --- | --- |
| `01-Overview.md` | Scope, fixed decisions, and first-stage goals. |
| `02-BindingModel.md` | Binding behavior, `IConfiguration` compatibility analysis, and intentional differences. |
| `03-JsonDocumentStore.md` | `_jsonObj`, write-back, unknown keys, and persistence behavior. |
| `04-SourceGenerator.md` | Descriptor generation, metadata sources, and diagnostics. |
| `05-Migration.md` | Versioned settings cleanup and validation rules. |
| `06-ImplementationPlan.md` | Ordered implementation phases, tests, and acceptance criteria. |
