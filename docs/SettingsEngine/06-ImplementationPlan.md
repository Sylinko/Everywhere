# Implementation Plan

## 1. Phase 0: Inventory

Tasks:

1. list all settings-reachable model types from `Settings`
2. identify settings-reachable types that need explicit JSON conversion support
3. list complex properties that must preserve runtime object references
4. list collections and decide first-stage collection policies
5. identify candidates for `SettingsSerializedSubtree`
6. define the initial attribute set

Acceptance criteria:

1. implementation has a known settings graph
2. SettingsEngine does not depend on string-only configuration leaves
3. `ColoredIcon` is selected as the serialized subtree test case

## 2. Phase 1: Attributes and Core Contracts

Tasks:

1. add `SettingsSerializedSubtreeAttribute`
2. add `SettingsUnknownMemberHandlingAttribute`
3. add `SettingsUnknownMemberHandling` enum
4. reserve merge metadata attributes only if needed for descriptor shape
5. define settings descriptor interfaces
6. define diagnostics model

Acceptance criteria:

1. attributes express the first-stage binding decisions
2. descriptor contracts can represent patch, serialized subtree, and unknown member policy

## 3. Phase 2: JSON Document Store

Tasks:

1. create the `JsonNode`-backed settings document store
2. reuse or extract path navigation from `WritableJsonConfiguration`
3. implement subtree replacement
4. implement unknown sibling preservation
5. implement prune behavior
6. implement debounced atomic save
7. implement diagnostics for parse and write failures

Acceptance criteria:

1. `_jsonObj` is the document source of truth
2. known path changes do not remove unknown sibling keys by default
3. file writes are atomic

## 4. Phase 3: Source Generator

Tasks:

1. generate descriptors from `Settings` root
2. follow System.Text.Json metadata
3. generate property accessors
4. generate JSON path metadata
5. generate policy metadata
6. generate collection shape metadata
7. generate diagnostics for unsupported shapes
8. integrate with existing source generator project or add a sibling generator

Acceptance criteria:

1. known settings properties can be read and written without runtime reflection
2. `JsonIgnore` and `JsonPropertyName` are honored
3. serialized subtree boundaries are represented as terminal descriptor nodes

## 5. Phase 4: Patch Binder

Tasks:

1. implement object patching from descriptor and `JsonNode`
2. patch existing complex objects by default
3. patch getter-only non-null complex objects
4. implement scalar conversion through System.Text.Json/descriptor readers
5. implement collection instance preservation with index/key patching
6. implement dictionary handling with unknown member policy
7. implement serialized subtree handling
8. preserve original object when serialized subtree deserialization fails

Acceptance criteria:

1. runtime `Settings` object can be patched from JSON
2. MVVM object references remain stable for patch-managed sections
3. failed serialized subtree reads keep original objects unchanged

## 6. Phase 5: Write-back

Tasks:

1. observe settings changes
2. map changed runtime property to descriptor path
3. write known value to `_jsonObj`
4. preserve unknown sibling keys
5. replace serialized subtree as one node
6. debounce and save

Acceptance criteria:

1. settings changes update `settings.json`
2. unknown keys are preserved by default
3. `IConfiguration.Set` is no longer needed

## 7. Phase 6: Settings Cleanup Migration

Tasks:

1. implement the narrow versioned cleanup migration
2. use hard-coded paths for known obsolete assistant fields
3. simplify legacy ARGB color objects to `SerializableColor` strings
4. remove obsolete assistant root generation options
5. validate cleaned JSON with SettingsEngine before writing
6. keep backup and atomic write semantics

Acceptance criteria:

1. existing typed settings JSON loads through SettingsEngine
2. cleanup is deterministic and idempotent
3. invalid cleanup shapes preserve the original file
4. migration does not introduce generic legacy scalar conversion

## 8. Phase 7: Remove IConfiguration Core Path

Tasks:

1. remove `IConfiguration` registration for settings
2. remove `WritableJsonConfiguration` as the core settings source
3. update `SettingsInitializer` to use Settings Engine
4. update call sites that used keyed `IConfiguration`
5. keep no write path through `IConfiguration`

Acceptance criteria:

1. app settings load and save through Settings Engine
2. old `IConfiguration` write path is gone
3. existing settings UI remains functional

## 9. Phase 8: Test Coverage

Required tests:

1. descriptor generation
2. JSON property naming and ignore behavior
3. patch existing object behavior
4. getter-only object patch behavior
5. collection instance preservation with item replacement
6. unknown key preservation
7. unknown key pruning
8. serialized subtree success
9. serialized subtree failure keeps original object
10. cleanup migration for obsolete assistant fields
11. ARGB color object cleanup
12. cleaned JSON stability

## 10. Deferred Work

Deferred to later projects:

1. semantic cloud-sync merge
2. prompt manager migration integration
3. WebDAV/RFC 6578 sync
4. settings history UI
5. comment-preserving JSON text edits
6. keyed collection patching
7. full multi-version sync conflict resolution
