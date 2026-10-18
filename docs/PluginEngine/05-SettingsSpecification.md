# Plugin Settings Specification

This document defines settings persistence and migrations. See the [engine specification](01-Specification.md)
for activation and cleanup, and the [settings author guide](06-SettingsDeveloperGuide.md) for usage.

**Persistence and migrations implemented; not yet released.** The generic engine, explicit model
metadata and temporary migration lifetime are available. Optional plugin-wide settings UI registration
is designed but remains a separate implementation batch; no plugin management page is provided.

## Scope and dependencies

A plugin may opt into the standard settings channel. Its settings model, document and runtime
instance are separate from Everywhere's built-in Settings. The channel reuses the existing patch,
observation, persistence and migration mechanisms; it does not introduce a second serializer or binder.

IPlugin and Everywhere.Plugin.Abstractions remain independent of application UI and settings types.
Everywhere.Plugin.Hosting manages activation and cleanup without interpreting settings pages.
Application-facing registration contracts belong alongside existing settings types in
Everywhere.Abstractions; Everywhere.Core implements the services and configuration integration.
The existing configuration generators provide opt-in authoring support; the plugin SDK does not
reference, ship or automatically import them.

Using this channel is an explicit dependency on the appropriate Everywhere application APIs.
Plugins that only reference the neutral plugin abstractions can continue to manage files through
DataDirectory. A standalone, application-independent configuration assembly is not required by
this design; introducing one would be a separate project-boundary decision.

The model, persistence engine and display entries are independent responsibilities:

| Responsibility | Contract |
|---|---|
| Settings model | Holds current values and change notifications |
| context.OpenSettingsAsync(...) | Prepares the plugin document, metadata and migrations, then returns its engine |
| SettingsEngine<TSettings> | Observes, patches and saves the model, without choosing a UI entry |
| ChatPlugin.SettingsItems | Supplies settings specific to one chat tool group through the existing chat tool page |
| Optional plugin-wide registry (planned) | Publishes a settings entry for a future plugin page, independently of chat tools |

A model can be persisted without any editor. Either display entry can exist without SettingsEngine.
A plugin may supply several tool-specific editors and optionally a plugin-wide editor, using shared
model sections or separate models. This does not imply separate persistence files or competing engines.
Plugin-wide describes the entry's scope; it does not merge data into the application's global Settings.

## Persistence and model ownership

The default path is `plugins-data/<plugin-id>/settings.json`, resolved through
IPluginContext.DataDirectory. Use the installation's logical PluginId, not a chat-tool Key or an
activation/installation ID. Updates share this data directory. Removing code does not delete settings.

SettingsEngine<TSettings> owns observation, patching and persistence. The internal SettingsInitializer
in Everywhere.Configuration adapts it to application startup, root Settings construction and built-in
migration registration.
The plugin owns its settings instance and engine. Models follow the existing observation and
serialization conventions; inheriting the application's concrete Settings class is not required.

Each engine has its own document, model, descriptor provider, JSON metadata/options, diagnostics
and observation lifetime. JSON remains the persistence source of truth; patch existing model objects
in place to preserve bindings. Reuse the current validation, unknown-member and save behavior.

The caller explicitly supplies JSON metadata/options for its model and reachable serialized types.
Declare a top-level, non-generic, non-static partial provider class with
`[GeneratedSettingsDescriptor(typeof(Model))]`. The plugin explicitly references the configuration generator;
application descriptor generation remains automatic. Use the existing SettingsItems generator for presentation. Do not add
plugin metadata to the application's static serializer context or global service registrations.

Call `context.OpenSettingsAsync(model, version, serializerOptions, descriptorProvider,
settingsFileName, configureMigrations, cancellationToken)` during initialization. TSettings is an observable reference
type implementing INotifyPropertyChanged. Version is a SemanticVersion describing the settings format.
The final three arguments are optional; settingsFileName defaults to settings.json under DataDirectory.
The opening extension borrows ILoggerFactory from context.Services. The caller owns each engine and must not open
competing writers for the same document. Stop the old activation
and finish its settings cleanup before opening the replacement writer. Opening settings is part of
plugin initialization and completes before publishing tools or settings UI that depend on them.

OpenSettingsAsync is provided by PluginSettingsExtensions in Everywhere.Core, under the
Everywhere.Configuration namespace. It is an extension over IPluginContext, not a member of the
neutral interface. Only plugins opting into the application settings APIs reference this implementation.
The extension resolves the data path and borrowed logging service, prepares migrations and metadata,
and creates the engine. SettingsEngine<TSettings> itself does not depend on IPluginContext or perform
plugin service resolution. The plugin entry still owns the returned engine's lifetime.

## Presentation is a separate contract

Persisting a model does not publish an editor. A chat tool can expose its model's items through the
existing ChatPlugin.SettingsItems property. A plugin-wide settings registry is planned independently
of chat tools; it is not part of this persistence implementation. Neither display route creates a
second writer or changes the settings file's ownership.

Custom control factories own dependency selection. `ISettingsControl.CreateControl()` and
`SettingsControlItem.CreateControl()` do not receive a provider from the presenter. A plugin can
construct `SettingsControl<TControl>` with its activation provider, an existing control, or a
parameterless factory that captures its required dependencies. Displaying settings does not create
or dispose a plugin service scope. Remove the host-held settings items and factory references before
the activation service scope retires.

## Lifetime and final saving

A plugin decides when settings edits affect its work and documents that boundary. The entry owns the
engine and awaits final saving during its normal disposal. IPluginContext.Register alone is
insufficient because ordinary process exit skips registered reversal.

Before disposing the engine, detach editors and stop and join work that can mutate the model.
Engine disposal stops observation, flushes pending changes and releases persistence resources.
Ordinary exit also saves settings without requiring a UI dispatcher. Forced termination cannot
guarantee a final save. UI registration and application shutdown coordination remain separate work.

The storage serializes background and explicit writes. Final FlushAsync joins an in-flight write even
if its snapshot already consumed the dirty flag, and reports exhausted write failures. Engine disposal
stops observation and joins background persistence even when final saving fails.

Completed initialization failure uses the same resource ownership rules for whatever was created.
A settings failure must not silently replace existing data with defaults. Diagnostics retained by
the host after cleanup must not retain plugin exception objects, types or migration delegates.

## Cache and JSON metadata lifetime

Use ConditionalWeakTable for a shared cache whose key is Type and whose value is a reusable,
type-only dictionary-key reader delegate. The value may refer back to its key; that association must
not alone keep a collectible type alive. A normal dictionary of weak keys with strongly held values
is not an equivalent replacement.

Pass the current JsonSerializerOptions into the cached reader on each call. The cached value must
not capture an engine's options, converters, model or services. A permanently live key such as
System.Int32 would otherwise retain plugin converters even in a weak-key table.

Engine-specific descriptor and serialization state stays with that engine. Existing instance caches
may remain strong when their owner is released with the activation; weak references are not required
everywhere. Do not inject plugin metadata into a global default resolver or use it as an implicit
fallback for plugin types. Preserve the existing conversion semantics while removing static
application-options assumptions from reader and generated-descriptor paths.

These changes remove host-owned roots introduced by this channel. They do not promise that arbitrary
converters or framework caches are collectible. Validate the whole serialization path and retain
RestartRequired when collection is unconfirmed. ConditionalWeakTable's value-to-key behavior is
specified by [the .NET API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.conditionalweaktable-2#remarks).

## Migration declarations and versions

Reuse SettingsMigration, ISettingsMigrationMetadata and AddSettingsMigration<T>. A plugin supplies
its own explicit migration registrations to a temporary initialization container. Never add those
registrations to the application root provider or discover all migrations across loaded assemblies.

Declare the current settings format version explicitly. It is independent of the package, host and
PluginEngine versions. Retain the existing semantic-version representation and document Version
field; Version records settings format progress, not the running application version. For both the
application and plugins, this field is document metadata, not a runtime model property. The engine
excludes it from model binding while preserving it in storage during normal saves. Pass the target
version to OpenSettingsAsync; a plugin model property mapped to the JSON name Version is rejected.

Each migration declares the target version it reaches. Targets are unique and ordered, and must
not exceed the declared current format version. Select pending migrations from version metadata
before constructing their instances. A false task result means no field changed; successful
execution still advances the version. Published migrations stay stable. Retain the history needed
for supported direct upgrades, and require a path to the declared target for older documents.

| Document state | Initialization behavior |
|---|---|
| No file | Use current defaults and initialize the current format version; do not replay historical migrations |
| Existing file without Version | Treat as the initial version, using the existing zero-version convention |
| Older supported version | Run the pending migration chain and validate the final document |
| Current version | Load without constructing migration instances |
| Newer version | Reject opening for writes and preserve the file; automatic downgrade is unsupported |

An invalid version is a settings error, not permission to overwrite the document. A newer file
blocks this plugin's settings initialization; it does not reset the document.

## Migration execution and disposal

The following target-version policy and cleanup-before-commit sequence apply to the plugin
OpenSettingsAsync/InitializeAsync path. The internal SettingsInitializer retains the application's historical Migrate()
policy: new files can run historical migrations, a higher document version is preserved, and saving
occurs before the caller disposes its temporary provider. Static selection still avoids constructing
already-applied migrations on that path.

1. Read the document and version before starting normal change observation.
2. Create a temporary provider and select pending registrations from metadata before resolving migration instances.
3. Back up an existing file before migration and apply pending tasks in version order to in-memory JSON.
4. Validate the resulting document using the caller's model contract and explicit JSON metadata.
5. Dispose migration instances and their owned dependencies, awaiting asynchronous disposal when needed.
6. Save the validated document and target version using the existing temporary-file replacement mechanism.
7. Release migration metadata/factories and open normal settings observation.

The existing migration operations remain synchronous JSON transformations. The settings initialization
entry point can be asynchronous to await cleanup; this does not require a new asynchronous migration
method. Inject owned dependencies through the temporary provider. Borrowed application services remain
owned by their existing provider and must not be disposed as migration-owned dependencies.

The temporary provider, registrations, resolver delegates and validators must not remain stored in
the long-lived settings engine after migration completes. They legitimately hold plugin types while
executing; deterministic ownership, not weak service references, ends that lifetime.

Migration, validation or temporary-resource cleanup failure before saving leaves the original file
unchanged and fails settings initialization. The temporary provider is disposed on both success and
failure, using ordinary MSDI ownership and DisposeAsync behavior. MSDI stops its disposal loop if a
service throws, so remaining services are not guaranteed a disposal callback; cleanup failure does
not authorize a retry of that disposed provider or a commit. Migration resources should finish their
own cleanup reliably. The engine does not introduce a replacement container to aggregate disposal
failures. Saving failures are
reported without falling back to defaults; no transaction with external files or databases is implied.

Once migration has been saved, later plugin initialization failure does not roll it back. A retry
reads the new version and skips completed migrations. Hot unload and ordinary exit do not rerun or
reverse migrations. Selecting older plugin code does not restore an older settings document.
Backups provide an explicit recovery option, not automatic activation rollback.

The standard guarantee covers transformation of this settings document. Avoid starting work,
publishing contributions or modifying external state in a migration. Auxiliary reads are possible,
but external writes and their recovery are outside the settings transaction.

## Implementation and validation closure

| Area | Current status |
|---|---|
| Persistence | Generic engine and application adapter share observation, patching and storage; plugins own final saving |
| JSON and generation | Per-engine options and descriptors; explicit plugin provider attribute and analyzer reference |
| Caches | Weak type-only dictionary-key cache; options passed on every call; instance-owned descriptor metadata |
| Migrations | Explicit temporary registrations, target-version policy, backup/validation and asynchronous cleanup before commit |
| Tests | Application settings, real collectible settings activations, isolated converters, migration selection, borrowed services, failure and retry |
| Presentation | Existing chat-tool property is available; optional plugin-wide registry and management page are separate work |

Before final merge, validate the complete plugin serialization and activation lifetime together with
the UI and service-boundary implementations. A weak cache alone is not evidence that arbitrary
converters, editor controls or framework caches can unload.
