# Binding Model

## 1. Core Model

Settings Engine should patch the existing runtime object graph by default.

```text
JsonNode subtree
  -> generated descriptor
  -> existing runtime object
  -> patch known properties
  -> keep unknown JSON keys in _jsonObj
```

This follows the useful part of `IConfiguration.Bind`: it avoids replacing objects unnecessarily and keeps MVVM references stable.

## 2. What IConfiguration Binder Does

The reference implementation is `3rd/Microsoft.Extensions.Configuration.Binder/src/ConfigurationBinder.cs`.

Important behaviors:

1. If a configuration section has a leaf string value, Binder converts it with `TypeDescriptor.GetConverter(type).ConvertFromInvariantString(value)`.
2. If the target property already has a non-null object instance, Binder recursively binds into that existing instance.
3. If the target property is null and writable, Binder tries to create a new instance.
4. Getter-only complex objects can still be patched when the getter returns a non-null instance.
5. Setters are called only when the binding point has a new value or an existing non-null value needs to be reassigned.
6. Missing configuration sections usually leave existing object values unchanged.
7. Dictionaries are populated by setting or adding keys on an existing dictionary instance.
8. Mutable collections are populated by adding items to the existing collection.
9. Arrays are recreated, but Binder copies existing array values and appends configured values.
10. Binder is string-first. Complex behavior comes from flattened configuration paths and `TypeConverter`.

This behavior is valuable for MVVM and long-lived object references, but it is not a perfect JSON document model.

## 3. Intentional Differences

Settings Engine should preserve Binder's object patch behavior, but collections should be more JSON-like.

Recommended first-stage rules:

| Target | Behavior |
| --- | --- |
| complex object | patch existing instance |
| getter-only complex object | patch existing instance if non-null |
| scalar property | convert and set if JSON exists |
| array | replace array value from JSON |
| `ObservableCollection<T>` / `IList<T>` | keep collection instance, patch items by index, append JSON extras, remove runtime tail |
| dictionary | keep dictionary instance, patch matching keys, add JSON extras, remove runtime keys missing from JSON |
| missing JSON property | leave runtime value unchanged |
| unknown JSON key | preserve in `_jsonObj` by default |

The key difference is collection handling. For settings, JSON should represent the current list state. If a user removes an array item from JSON, the runtime collection should not keep the removed item.

## 4. Serialized Subtree Boundary

Use:

```csharp
[SettingsSerializedSubtree]
```

This means the marked property or type is handled as one `System.Text.Json` subtree.

Rules:

1. The subtree is deserialized with the configured `JsonTypeInfo<T>` or converter.
2. Normal descriptor patching stops at this boundary.
3. Child properties cannot opt back into Settings Engine patching.
4. If deserialization succeeds and the property is writable, the property is replaced.
5. If deserialization fails, the existing runtime object is left unchanged and a warning is recorded.
6. The boundary is suitable for polymorphic objects, immutable records, and types whose invariants are best owned by `System.Text.Json`.

`ColoredIcon` should be the first validation case for this behavior.

## 5. Polymorphism

Settings Engine should not infer polymorphism for ordinary objects.

Polymorphic or abstract/interface-backed values should be explicit serialized subtrees. This avoids hidden factory behavior and makes replacement boundaries visible.

For `ColoredIcon`, the JSON may keep a familiar discriminator shape:

```json
{
  "Type": "Lucide",
  "Kind": "Bot",
  "Foreground": "#FFFFFFFF"
}
```

The C# model can move toward a base type plus derived types while preserving a stable JSON discriminator.

## 6. Unknown Members

Default policy:

```text
Preserve
```

This means unknown JSON keys stay in `_jsonObj` when known properties are patched or written back.

Explicit policy:

```csharp
[SettingsUnknownMemberHandling(SettingsUnknownMemberHandling.Prune)]
```

Use prune for strict machine-owned objects, generated flags dictionaries, or places where stale keys are harmful.

Possible policies:

| Policy | Meaning |
| --- | --- |
| `Preserve` | Keep unknown JSON keys. Default. |
| `Prune` | Remove keys not known by the descriptor. |
| `Error` | Report diagnostics when unknown keys are present. |

## 7. Collection Policy

Settings Engine has one collection policy for now:

1. keep mutable collection and dictionary instances
2. patch existing object items when the JSON shape allows it
3. replace scalar or serialized subtree items by index/key
4. add values present in JSON
5. remove mutable runtime items or keys that are absent from JSON

There is no keyed collection binding policy in the runtime. If dynamic reload
or semantic merge requires identity-aware collections later, that policy should
be introduced as a separate design.

## 8. Failure Handling

Runtime read failure should be conservative.

Rules:

1. Serialized subtree failure keeps the old object unchanged.
2. Scalar conversion failure keeps the old value unchanged during normal load.
3. Cleanup migration failure preserves the original file and stops startup.
4. All failures should produce diagnostics for the settings page or logs.

Migration should stay narrower than runtime binding. It should not add broad legacy scalar conversion behavior to the runtime binder.
