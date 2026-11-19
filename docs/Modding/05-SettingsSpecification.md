# Mod Settings Specification

**Status:** implemented via generic `SettingsEngine<TSettings>`, `GeneratedSettingsDescriptorAttribute`, `IModSettingsRegistry`, and `SettingsMigrator.InitializeAsync`.

## 1. Persistence and Model Ownership

- `SettingsEngine<TSettings>` in `Everywhere.Core.Configuration.Engine` operates on any `[GeneratedSettingsItems]` class rather than hard-coding the application `Settings` root.
- `ModSettingsExtensions.OpenSettingsAsync<TSettings>(this IModContext context, TSettings settings, SemanticVersion version, JsonSerializerOptions serializerOptions, ISettingsDescriptorProvider descriptorProvider, string settingsFileName = "settings.json", Action<IServiceCollection>? configureMigrations = null, CancellationToken cancellationToken = default)` opens `{DataDirectory}/{settingsFileName}`, runs optional migrations, patches the caller-owned `settings` object in place, and returns a disposable `SettingsEngine<TSettings>` that writes debounced edits back atomically.
- Host-level application settings use `SettingsInitializer` (`AsyncInitializerIndex.Settings`), keeping `SettingsInitializer` internal to startup while mods own their returned `SettingsEngine<TSettings>`.

## 2. Descriptors and JSON Metadata Isolation

- Mods annotate a partial helper class with `[GeneratedSettingsDescriptor(typeof(MySettings))]`.
- `Everywhere.Configuration.SourceGenerator` generates an `ISettingsDescriptorProvider` implementation for that class without touching the host `Settings` root.
- Mods supply their own source-generated `JsonSerializerOptions` (`[JsonSerializable(typeof(MySettings))]` `JsonSerializerContext`) so STJ metadata stays inside the mod's collectible `AssemblyLoadContext`.

## 3. UI Control and Mod Settings Registry

- `ISettingsControl.CreateControl()` is parameterless; `SettingsControlPresenter` resolves controls without coupling `SettingsControl` contracts to host-only types.
- `IModSettingsRegistry` (`ModSettingsRegistry` in `Everywhere.Core.Configuration`) manages optional mod-wide UI entries:
  - Registration returns an `IAsyncDisposable` handle (detached synchronously/on UI dispatcher when unloaded, setting the retained `SettingsItemsControl.ItemsSource` to `null` so views cannot pin unloaded settings models).
  - Tool-specific settings exposed via `ChatPlugin.SettingsItems` are separate from `IModSettingsRegistry` and appear directly in the chat plugin page.

## 4. Migrations

- Concrete migrations inherit `SettingsMigration` and implement `ISettingsMigrationMetadata` (`static abstract SemanticVersion Version { get; }`).
- `SettingsMigrator.InitializeAsync` creates an isolated transient `ServiceProvider` for pending migrations (never disposing caller-owned host singletons), validates migrated JSON before writing atomically with backup, and disposes migration resources (`IAsyncDisposable` / `IDisposable`) before committing.
- Newer or invalid `"Version"` values in `settings.json` fail fast with `InvalidDataException` instead of silently resetting user configuration.
- `SettingsEngine<TSettings>.DisposeAsync()` cancels pending debounced writes cleanly and flushes any dirty state atomically on both hot unload and process exit.
