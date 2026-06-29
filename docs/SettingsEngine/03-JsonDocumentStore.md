# JSON Document Store

## 1. Source of Truth

The settings file is represented by an in-memory JSON document:

```text
JsonNode _jsonObj
```

`_jsonObj` is the persistence source of truth. The runtime `Settings` object is the app-facing state. Generated descriptors connect the two.

## 2. Why Not Plain Serialization

Plain serialization has several problems for settings:

1. it replaces whole object graphs
2. it drops unknown keys
3. it cannot express missing vs null vs present as easily
4. it makes compatibility with older or newer app versions harder
5. it can break MVVM references

Settings files behave more like user documents than DTO payloads.

## 3. Relationship to VS Code-style Settings

VS Code's settings model is a useful reference point: user settings are updated by editing specific JSON paths, which preserves unmanaged keys and, in VS Code's text-editing model, comments.

Everywhere should adopt the same preservation principle for unknown keys. However, the first Settings Engine is based on `JsonNode`, so comments and duplicate property spelling are not preserved.

First-stage guarantee:

```text
unknown JSON keys are preserved by default
```

Not guaranteed:

```text
comments are preserved
duplicate object properties are preserved
formatting is preserved exactly
```

## 4. WritableJsonConfiguration Reuse

`3rd/WritableJsonConfiguration` is useful because it already has:

1. a `JsonNode` document field
2. path navigation
3. subtree replacement
4. flattened configuration data generation
5. debounced saving
6. atomic file write behavior

Settings Engine should reuse or extract these ideas, but it should not keep `IConfigurationProvider.Data` as the main model. Flattened string data may exist only as a temporary compatibility view.

## 5. Read Pipeline

```text
read settings.json bytes
  -> parse JsonNode
  -> run migration if needed
  -> keep _jsonObj
  -> patch existing Settings object
  -> emit diagnostics
```

Parsing should accept the same relaxed JSON behavior as the current settings path where practical:

1. trailing commas
2. comments skipped during parsing
3. duplicate properties tolerated by the parser

The canonical writer does not need to reproduce comments or duplicate entries.

## 6. Write Pipeline

Runtime setting changes should update `_jsonObj` locally.

```text
Settings object changed
  -> descriptor resolves JSON path
  -> serialize known property value to JsonNode
  -> replace that known subtree
  -> preserve unknown siblings unless prune policy is active
  -> debounce and atomically write settings.json
```

For a serialized subtree, the whole marked subtree is replaced.

For normal patch-managed objects, only known changed paths should be replaced.

## 7. Unknown Key Preservation

Unknown keys are preserved because `_jsonObj` remains the source document.

Example:

```json
{
  "Display": {
    "Theme": "Dark",
    "OldSettingFromPreviousVersion": true
  }
}
```

Changing `Display.Theme` should not remove `OldSettingFromPreviousVersion` unless `Display` or that specific subtree is marked with prune behavior.

## 8. Prune Behavior

Pruning is opt-in.

Prune should be used for:

1. machine-owned generated maps
2. flags dictionaries where stale keys are harmful
3. strict schema objects
4. temporary migration output sections

Prune should not be the default for user-facing settings sections.

## 9. Atomicity and Backup

File writes should be atomic:

1. serialize to memory
2. write to a temporary file
3. flush
4. replace the target file

Major migrations should keep a pre-migration backup or history snapshot before writing canonical JSON.

## 10. Diagnostics

Settings Engine should collect diagnostics for:

1. parse failure
2. migration fallback
3. scalar conversion failure
4. serialized subtree failure
5. unknown member errors for strict sections
6. write failure

Diagnostics are required for a future settings sync page and are useful even before sync exists.
