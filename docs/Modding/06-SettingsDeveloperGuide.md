# Mod Settings Developer Guide

## 1. Define Model, Descriptor, and JsonSerializerContext

```csharp
[GeneratedSettingsItems]
public sealed partial class ExampleSettings : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.Example_Endpoint_Header, LocaleKey.Example_Endpoint_Description)]
    public partial string? Endpoint { get; set; }
}

[GeneratedSettingsDescriptor(typeof(ExampleSettings))]
public partial class ExampleSettingsDescriptors;

[JsonSerializable(typeof(ExampleSettings))]
[JsonSerializable(typeof(string))]
internal partial class ExampleSettingsJsonSerializerContext : JsonSerializerContext;
```

## 2. Open Settings During Mod Initialization

```csharp
var settings = new ExampleSettings();
_settings = await context.OpenSettingsAsync(
    settings,
    new SemanticVersion(1),
    ExampleSettingsJsonSerializerContext.Default.Options,
    new ExampleSettingsDescriptors(),
    configureMigrations: services => services.AddSettingsMigration<V1Migration>(),
    cancellationToken: cancellationToken).ConfigureAwait(false);
```

Always dispose `_settings` (`await _settings.DisposeAsync().ConfigureAwait(false)`) in a `finally` block inside `IMod.DisposeAsync()` after detaching any UI views referencing `settings.SettingsItems` so final edits flush even on `ModStopReason.Exit`.

## 3. Add a Versioned Migration

Implement `SettingsMigration` and `ISettingsMigrationMetadata`:

```csharp
internal sealed class V1Migration : SettingsMigration, ISettingsMigrationMetadata
{
    public static SemanticVersion Version { get; } = new(1);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [Normalize];

    private static bool Normalize(JsonObject root)
    {
        if (root["LegacyKey"] is not { } node) return false;
        root["Endpoint"] ??= node.DeepClone();
        root.Remove("LegacyKey");
        return true;
    }
}
```

If a migration resolves dependencies via `IServiceProvider`, register them in `configureMigrations`. Any migration that implements `IDisposable` or `IAsyncDisposable` is cleaned up before the migrated JSON is atomically written to disk.

## 4. Expose Settings in UI

- **Tool settings tab**: Return `settings.SettingsItems` from `ModChatPlugin.SettingsItems`. `ChatPluginPageViewModel` detaches it automatically when the mod unloads or selection changes.
- **Mod-wide settings registry (optional)**: Register via `IModSettingsRegistry` (`context.Register(...)`) to provide a mod-level `SettingsItems` entry. Because `SettingsItemsControl` clears `ItemsSource` on retirement, retained closed views do not keep the mod's `AssemblyLoadContext` alive.
