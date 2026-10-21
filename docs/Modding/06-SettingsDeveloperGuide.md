# Mod Settings Developer Guide

Read the [Settings Specification](05-SettingsSpecification.md) for the contract and the
[main developer guide](02-ModDeveloperGuide.md) for mod lifecycle and packaging.

**Status: implemented in the working tree; not yet merged or released.** SettingsEngine<TSettings>,
mod descriptor generation, IModSettingsRegistry and migration initialization are available.
The chat tool page uses ChatPlugin.SettingsItems for tool-specific settings. The independent,
optional mod-wide registry is available; its general mod management page is not implemented.

## Define a separate settings model

Define a mod-owned model using the existing notification and configuration conventions.
It does not inherit Everywhere's concrete Settings class and is not registered as that singleton.
Supply JSON metadata/options explicitly for the model and all serialized types it uses. The existing
configuration generator accepts explicit mod roots. Reference this analyzer explicitly; the SDK
does not include it or assume that a mod uses Everywhere's configuration APIs.

For a repository-based project, use an ordinary analyzer ProjectReference (adjust the relative path):

```xml
<ProjectReference Include="../../src/Everywhere.Configuration.SourceGenerator/Everywhere.Configuration.SourceGenerator.csproj"
                  ReferenceOutputAssembly="false" OutputItemType="Analyzer" />
```

If using a separately supplied generator DLL, add it through an explicit Analyzer item instead.
Importing a host installation supplies runtime API references, not this optional authoring tool.
The generator is a compile-time dependency and must not be added to the mod runtime payload.

For a UI, use the existing GeneratedSettingsItems facility or provide SettingsItems yourself.
Custom controls select dependencies at factory creation, for example:

```csharp
var control = new SettingsControl<MyControl>(context.Services);
var factory = new SettingsControl<MyControl>(
    () => new MyControl(context.Services.GetRequiredService<IMyService>()));
```

The first form requires a corresponding service registration. The second can construct the control
directly. Both defer creation until presentation. The provider overload defaults to `isCacheEnabled: false`,
so each presentation resolves the control according to its service registration. The delegate overload
defaults to `true` and retains its first result. Pass `isCacheEnabled` explicitly to choose the other
behavior. An existing-control overload always returns the supplied instance.
The presenter calls `CreateControl()` and never
supplies the application root. The factory borrows the provider; it must not dispose it. Remove UI
contributions before activation retirement and stop using their factories afterward.
Settings persistence and presentation remain separate: a mod can save settings without providing
a page, and opening a page must not create a second writer for the same file.

The standard location is:

```text
mods-data/<mod-id>/settings.json
```

Resolve it from IModContext.DataDirectory. Use the logical mod ID, not the chat-tool Key
such as mod.everything. Rebuilds and updates keep the same data directory. Do not put writable
settings in InstallationDirectory or delete them merely because the mod is unloaded.

The channel uses application APIs, so add the appropriate optional Everywhere references when using
it. Mods that only use Everywhere.Modding.Abstractions can still manage DataDirectory themselves.
There is no SettingsItems property on IMod and no UI dependency in the neutral engine.

## Generate model metadata

Use a top-level, non-generic, non-static partial provider class. JSON metadata is separate from settings
descriptors: supply a JsonSerializerContext covering the root and reachable serialized types, including
dictionary key types. Both declarations live in the mod assembly.

```csharp
[GeneratedSettingsDescriptor(typeof(SearchSettings))]
public partial class SearchSettingsDescriptors;

[JsonSerializable(typeof(SearchSettings))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal partial class SearchSettingsJson : JsonSerializerContext;

[GeneratedSettingsItems]
public sealed partial class SearchSettings : ObservableObject
{
    [DynamicLocaleKey("Search_IsEnabled", "Search_IsEnabledDescription")]
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public int MaxResults
    {
        get => _maxResults;
        set => SetProperty(ref _maxResults, value);
    }

    private bool _isEnabled = true;
    private int _maxResults = 100;
}
```

This uses Everywhere.Configuration, Everywhere.I18N, System.Text.Json.Serialization and
CommunityToolkit.Mvvm.ComponentModel. Define the locale keys in your mod resources and register
them through the normal mod localization path. GeneratedSettingsItems exposes the SettingsItems
property and uses compiled bindings. The locale attribute makes IsEnabled a presented setting;
MaxResults in this small example is persisted without a settings item. Apply the existing settings
item attributes to expose more fields.

## Initialize and own the engine

Import Everywhere.Configuration to use context.OpenSettingsAsync. This extension is implemented in
Everywhere.Core; using the settings channel requires an application API reference. It adds no members
or dependencies to Everywhere.Modding.Abstractions. The returned SettingsEngine<TSettings> owns the
running document; the internal SettingsInitializer is only the application's startup adapter.

Open settings once during InitializeAsync, before publishing dependent tools or UI. Retain the engine
on the mod entry. The generic type must implement INotifyPropertyChanged; it need not inherit
Everywhere's Settings. A typical entry body is:

```csharp
private SettingsEngine<SearchSettings>? _settings;
private IAsyncDisposable? _settingsPage;

public async ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
{
    var model = new SearchSettings();
    _settings = await context.OpenSettingsAsync(
        model,
        new SemanticVersion(2, 0, 0),
        SearchSettingsJson.Default.Options,
        new SearchSettingsDescriptors(),
        configureMigrations: services => services.AddSettingsMigration<RenameSearchLimit>(),
        cancellationToken: cancellationToken);

    var registry = context.Services.GetRequiredService<IModSettingsRegistry>();
    _settingsPage = registry.Register(context, () => model.SettingsItems);
    context.Register(_settingsPage);
}

public async ValueTask DisposeAsync()
{
    try
    {
        if (_settingsPage is not null) await _settingsPage.DisposeAsync();
        // Stop and join any mod work that can still change the model here.
    }
    finally
    {
        if (_settings is not null) await _settings.DisposeAsync();
    }
}
```

The entry implements IMod and IAsyncDisposable. The opening extension uses Everywhere.Configuration;
engine and version names use Everywhere.Configuration.Engine and Everywhere.Common; service helpers use
Microsoft.Extensions.DependencyInjection. The migration below defines RenameSearchLimit. Omit the
migration callback when no older format is supported; a fresh file starts directly at the declared
version. Use an explicit options instance and serializer context when you need custom converters.
Do not open another engine for the same document when creating a view.
The optional settingsFileName parameter selects a file under DataDirectory and defaults to settings.json;
choosing a different display entry does not select or create a different file.

Entry disposal saves on both hot unload and application exit. Merely calling context.Register on the
engine is insufficient: ordinary exit skips registered reversal. The page handle is idempotent, so
entry disposal can close it early and later context reversal can await it again. On hot stop this
closure detaches bindings before the final save. On exit it drops registry ownership without waiting
for the dispatcher. IModContext.StopReason is set before Stopping: Unload includes initialization
failure, and Exit means process shutdown. Authors do not need to branch when using the standard handle.

If initialization fails, the host disposes the entry and registered resources that were created.
Do not replace an unreadable or incompatible document with defaults. Stop external writers before
flushing; a save failure is reported rather than silently treated as success. Forced termination
cannot guarantee persistence.

## Register an optional mod-wide settings entry

IModSettingsRegistry is an application service obtained from context.Services. Register accepts
an activation context and Func<SettingsItems>, and returns an asynchronous removal handle. Retain
that handle on the entry and also register its reversal responsibility with context.Register.

The factory is invoked on the UI thread only when a consumer calls registry.CreateView(modId).
That method must be called on the UI thread; it returns the same Control while the registration is
active, or null when unavailable. It never loads a mod. A future hot-reload entry point must drain ongoing UI retirement before shutting down
the dispatcher; switching to Exit does not retroactively replace a hot stop already in progress.
One live registration per mod ID is
accepted; use SettingsItems groups for multiple categories. A handle from an old activation cannot
remove a replacement's entry.

The future management page will show installed mods on the left and details, README and settings
on the right. Disabled, skipped or unloaded mods have no typed settings editor in this design.
They are not activated just to render one. Persistence works without any page being opened.

This entry is optional even when the mod uses SettingsEngine. Conversely, a registered editor
can use another persistence mechanism or edit non-persistent state. It belongs to the whole mod;
it does not replace settings supplied by an individual chat tool.

## Expose settings for a chat tool

Override the existing ChatPlugin.SettingsItems property. The model may be the same instance owned
by SettingsEngine, but the display contract does not depend on that engine:

```csharp
// In the chat tool subclass, _settings is the mod-owned settings model.
public override IReadOnlyList<SettingsItem>? SettingsItems => _settings.SettingsItems;

// In the mod entry, publish the tool through the normal chat registration.
_toolsRegistration = context.Services.GetRequiredService<IChatPluginManager>()
    .RegisterChatPlugin(tools);
context.Register(_toolsRegistration);
```

No IModSettingsRegistry registration is needed for this route.
Each tool group can expose its own settings. A mod may additionally register a mod-wide entry,
showing common settings or other model sections. Sharing a model does not require sharing a Control
between display locations, nor does adding an editor create another settings document.

When a tool disappears, the chat page clears selection and releases the old settings tab's item
source, notifying its bound view. Registration disposal drains tool calls but does not wait for UI
delivery. Coordinate UI detachment before disposing a shared settings model or saving its final state.
Ordinary Exit must not depend on a live dispatcher. See the
[chat registration contract](04-ChatToolIntegration.md#registration-contract).

Document when edits affect running work. Capture UI-owned preferences on the UI thread before
starting background operations if they need a consistent snapshot. Persistence does not make a
sequence of separate user edits transactional.

Prefer the existing GeneratedSettingsItems path, which is covered by the collectible settings fixture.
A reflection Binding comparison retained the mod assembly; the specific framework root is not yet
diagnosed. Custom controls, subscriptions and framework registrations retain the normal UI ownership
limits. Page removal alone does not prove collection; use the engine result and honor RestartRequired.

## Add a migration

The existing migration class and registration helper are retained. For example, a settings format
upgrade can rename a field without depending on the current CLR settings model:

```csharp
public sealed class RenameSearchLimit : SettingsMigration, ISettingsMigrationMetadata
{
    /// <inheritdoc />
    public static SemanticVersion Version { get; } = new(2, 0, 0);

    /// <inheritdoc />
    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
        [RenameProperty];

    private static bool RenameProperty(JsonObject root)
    {
        return TryMoveProperty(root, "SearchLimit", "MaxResults");
    }
}
```

This uses Everywhere.Configuration, Everywhere.Common and System.Text.Json.Nodes, plus the
normal generic collection/delegate imports. TryMoveProperty uses the existing destination replacement
behavior; handle a pre-existing destination explicitly if your migration needs a different policy.

In the OpenSettingsAsync migration-configuration callback, register it using the existing helper:

```csharp
services.AddSettingsMigration<RenameSearchLimit>();
```

Here services is the temporary migration service collection for your settings initialization, not
Everywhere's root container. Constructor dependencies and custom factories can use the existing
registration mechanism. Ordinary field transformations need neither a container dependency nor
an instance of an obsolete settings class.

Migration metadata is inspected before instances are created. Only pending migrations are resolved,
then they execute in ascending target-version order. A task returning false means that this file
needed no field change; it still completes that migration. Throw on an actual migration failure.

Do not change an already published migration to implement a later upgrade. Add another target
version, keep targets unique, and retain the chain required by your supported upgrade paths.
Operate on the old JSON shape, not assumptions taken from the current model's default values.

## Declare the settings format version

Pass the target SemanticVersion to OpenSettingsAsync separately from the mod package version. For example, mod releases 1.2.0 and 1.3.0 can both use settings format 2.0.0.
The document's Version field records the settings format; do not use it for the Everywhere version.
It is document metadata for both the application and mods, not a property of the runtime settings
model. The engine excludes it from model binding and preserves it during observed updates. A mod
model property mapped to the JSON name Version is rejected.

A new installation starts with current defaults and the declared target version without replaying
old migrations. An existing versionless file uses the initial zero version. Older supported files
run the declared chain; a current file constructs no migration instances. A file from a newer
settings format is preserved and rejected for writing rather than silently downgraded.

## Migration resources, failure and retry

Migrations run before normal observation begins. They share one temporary initialization provider,
which owns their instances and any dependencies it creates. Cleanup awaits asynchronous resources
when needed, including after failure. Borrowed application services keep their original ownership.
The standard settings opening path manages this lifetime; authors should not keep migration
instances or register migration factories in long-lived host services.

Changes are made to in-memory JSON, validated, and saved after temporary migration-resource cleanup.
An existing document is backed up before migration. A migration, validation or cleanup failure before
saving leaves that document unchanged and fails initialization. The backup is an explicit recovery
option, not an instruction to overwrite user settings automatically.

MSDI's normal DisposeAsync behavior applies: if one service throws while being disposed, disposal of
remaining services stops. The settings file is still not committed. Do not rely on another service's
disposal callback running after a failure, and do not retry a disposed provider. Prefer reliable,
self-contained migration cleanup. Register borrowed service instances directly with AddSingleton(instance)
or pass them into migration constructors without transferring their ownership; a factory returning a
borrowed disposable object can cause the temporary provider to capture and dispose it.

If migration saves successfully and a later mod initialization step fails, the new settings version
remains. The next attempt skips those completed migrations. Unload and exit do not undo migrations,
and switching back to old mod code does not roll back data. Restore a suitable backup explicitly
when recovery requires an older format.

Keep migration tasks focused on JSON transformation. Reading auxiliary data can be useful, but
external file/database writes, process startup and application registrations are not covered by
settings rollback. Tasks remain synchronous; asynchronous provider cleanup does not change the
migration task API.

## JSON metadata and unload expectations

Keep the model, serializer metadata, converters and descriptor provider within your settings engine's
lifetime. The shared dictionary-key cache uses weak Type keys and reusable delegates that
receive options per call. It must not capture your options under a permanently live host type.

Generated metadata supports predictable lifetime but is not a guarantee for arbitrary converters,
controls or third-party caches. Do not retain mod settings or migration objects in global host
collections. The host must still confirm collection; RestartRequired remains the outcome when
unloading cannot be confirmed.

The maintained tests cover isolated documents, final saves, mod enum keys, activation-specific
converters for host integer keys, generated settings edits, migration failure/retry and confirmed
collectible unloading. See the [validation record](03-Validation.md#mod-settings) for the exact
scope. Application management UI and arbitrary Avalonia cache cleanup are not included.
