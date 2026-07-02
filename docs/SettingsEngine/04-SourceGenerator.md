# Source Generator

## 1. Purpose

The source generator produces Settings descriptors from the `Settings` root model.

Descriptors make Settings Engine AOT-friendly and avoid runtime reflection for known settings properties.

## 2. Root-based Generation

Generation starts from:

```text
Everywhere.Configuration.Settings
```

The generator should walk the reachable settings object graph and produce descriptors for known properties.

Do not introduce section attributes like `[SettingsSection("Display")]` when the JSON structure can be derived from the root model.

## 3. Metadata Sources

The generator should follow System.Text.Json metadata:

1. `JsonIgnore`
2. `JsonPropertyName`
3. `JsonConverter`
4. nullable annotations
5. public getters and setters
6. constructor shape where relevant

Everywhere-specific attributes should describe Settings Engine behavior only:

1. serialized subtree boundary
2. unknown member handling
3. collection binding behavior
4. future merge key metadata
5. future sync metadata

## 4. Generated Descriptor Shape

Each descriptor should contain enough information to:

1. resolve a JSON property name
2. resolve the JSON path from the root
3. get the current runtime property value
4. set a runtime property value where writable
5. patch child properties where patching is allowed
6. serialize a known value back to `JsonNode`
7. read a known value from `JsonNode`
8. identify serialized subtree boundaries
9. apply unknown member policy
10. apply collection binding policy

The exact generated API can evolve, but it should keep these responsibilities explicit.

## 5. Runtime Combination with System.Text.Json

Source generators cannot directly depend on each other's generated output.

Settings Engine should combine metadata at runtime:

```text
Settings descriptor graph
  + JsonSerializerOptions / JsonTypeInfo resolver
  -> read/write/patch behavior
```

The Settings descriptor provides paths, accessors, and policies. System.Text.Json provides type-specific serialization where the engine needs full value conversion or serialized subtree handling.

## 6. Serialized Subtree Descriptor

When a property or type is marked:

```csharp
[SettingsSerializedSubtree]
```

the descriptor should mark the node as terminal.

Terminal means:

1. no generated child patching below this point
2. use `JsonSerializer` / `JsonTypeInfo<T>` for the whole subtree
3. preserve original runtime object if reading fails
4. replace the runtime property only after successful deserialization

If the attribute is on a type, all uses of that type are terminal unless a future design explicitly supports overrides. First-stage design should not support opting back into patching below that type.

## 7. Unknown Member Policy Descriptor

Unknown member policy can be declared on a type or property.

Default:

```text
Preserve
```

Generated descriptors should resolve the effective policy by walking from property metadata to type metadata to default.

## 8. Collection Policy Descriptor

Generated descriptors should mark collection shape:

1. array
2. mutable list
3. observable collection
4. dictionary
5. unsupported collection

First-stage default for mutable collections is to keep the instance and replace items to match JSON.

Future metadata may add keyed collection patching:

```csharp
[SettingsMergeKey(nameof(Id))]
```

This is reserved for later semantic sync and does not need full implementation in stage one.

## 9. Diagnostics

Generator diagnostics should report:

1. settings-reachable types with unsupported abstract/interface properties
2. converter-only types that have no Settings Engine or System.Text.Json path
3. unsupported collection shapes
4. serialized subtree attributes on unwritable properties that cannot be replaced
5. conflicting Settings Engine attributes
6. properties ignored by JSON but still marked with Settings Engine metadata

Diagnostics should be actionable and should point to the model property.

## 10. Relationship to Existing Generator

`Everywhere.Configuration.SourceGenerator` already generates settings UI metadata.

The Settings Engine generator can be added to the same project or as a sibling generator. It should share symbol utilities where useful, but it should not couple persistence behavior to UI item generation.

Settings UI metadata and Settings Engine descriptors are different products:

| Generator output | Purpose |
| --- | --- |
| settings UI items | render configuration UI |
| settings descriptors | read, patch, write, migrate, and later merge settings |
