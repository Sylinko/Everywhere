# Mod Settings Specification

This document owns the mod settings contract. See the [engine specification](01-Specification.md)
for activation and cleanup, the [settings author guide](06-SettingsDeveloperGuide.md) for usage,
and the [documentation index](README.md) for other integration topics.

**Implemented in the working tree; not yet merged or released.** The generic engine, explicit model
metadata, settings UI registry and temporary migration lifetime are implemented. The mod management
page is outside this implementation. The [validation record](03-Validation.md#mod-settings) describes
coverage and limits; desktop acceptance and final review remain required before merge.

## Scope and dependencies

A mod may opt into the standard settings channel. Its settings model, document and runtime
instance are separate from Everywhere's built-in Settings. The channel reuses the existing patch,
observation, persistence and migration mechanisms; it does not introduce a second serializer or binder.

IMod and Everywhere.Modding.Abstractions remain independent of application UI and settings types.
Everywhere.Modding.Hosting manages activation and cleanup without interpreting settings pages.
Application-facing registration contracts belong alongside existing settings types in
Everywhere.Abstractions; Everywhere.Core implements the services and configuration integration.
The existing configuration generators provide opt-in authoring support; the mod SDK does not
reference, ship or automatically import them.

Using this channel is an explicit dependency on the appropriate Everywhere application APIs.
Mods that only reference the neutral mod abstractions can continue to manage files through
DataDirectory. A standalone, application-independent configuration assembly is not required by
this design; introducing one would be a separate project-boundary decision.

The model, persistence engine and display entries are independent responsibilities:

| Responsibility | Contract |
|---|---|
| Settings model | Holds current values and change notifications |
| context.OpenSettingsAsync(...) | Prepares the mod document, metadata and migrations, then returns its engine |
| SettingsEngine<TSettings> | Observes, patches and saves the model, without choosing a UI entry |
| ChatPlugin.SettingsItems | Supplies settings specific to one chat tool group through the existing chat tool page |
| IModSettingsRegistry | Publishes an optional mod-wide settings entry for a future mod page, independently of chat tools |

A model can be persisted without any editor. Either display entry can exist without SettingsEngine.
A mod may supply several tool-specific editors and optionally a mod-wide editor, using shared
model sections or separate models. This does not imply separate persistence files or competing engines.
Mod-wide describes the entry's scope; it does not merge data into the application's global Settings.

## Persistence and model ownership

The default path is `mods-data/<mod-id>/settings.json`, resolved through
IModContext.DataDirectory. Use the installation's logical ModId, not a chat-tool Key or an
activation/installation ID. Updates share this data directory. Removing code does not delete settings.

SettingsEngine<TSettings> owns observation, patching and persistence. The internal SettingsInitializer
in Everywhere.Configuration adapts it to application startup, root Settings construction and built-in
migration registration.
The mod owns its settings instance and engine. Models follow the existing observation and
serialization conventions; inheriting the application's concrete Settings class is not required.

Each engine has its own document, model, descriptor provider, JSON metadata/options, diagnostics
and observation lifetime. JSON remains the persistence source of truth; patch existing model objects
in place to preserve bindings. Reuse the current validation, unknown-member and save behavior.

The caller explicitly supplies JSON metadata/options for its model and reachable serialized types.
Declare a top-level, non-generic, non-static partial provider class with
`[GeneratedSettingsDescriptor(typeof(Model))]`. The mod explicitly references the configuration generator;
application descriptor generation remains automatic. Use the existing SettingsItems generator for presentation. Do not add
mod metadata to the application's static serializer context or global service registrations.

Call `context.OpenSettingsAsync(model, version, serializerOptions, descriptorProvider,
settingsFileName, configureMigrations, cancellationToken)` during initialization. TSettings is an observable reference
type implementing INotifyPropertyChanged. Version is a SemanticVersion describing the settings format.
The final three arguments are optional; settingsFileName defaults to settings.json under DataDirectory.
The opening extension borrows ILoggerFactory from context.Services. The caller owns each engine and must not open
competing writers for the same document. Stop the old activation
and finish its settings cleanup before opening the replacement writer. Opening settings is part of
mod initialization and completes before publishing tools or settings UI that depend on them.

OpenSettingsAsync is provided by ModSettingsExtensions in Everywhere.Core, under the
Everywhere.Configuration namespace. It is an extension over IModContext, not a member of the
neutral interface. Only mods opting into the application settings APIs reference this implementation.
The extension resolves the data path and borrowed logging service, prepares migrations and metadata,
and creates the engine. SettingsEngine<TSettings> itself does not depend on IModContext or perform
mod service resolution. The mod entry still owns the returned engine's lifetime.

## Chat-tool settings

Custom control factories own dependency selection. `ISettingsControl.CreateControl()` and
`SettingsControlItem.CreateControl()` do not receive a provider from the presenter. A mod can
construct `SettingsControl<TControl>` with its activation provider, an existing control, or a
parameterless factory that captures its required dependencies. Displaying settings does not create
or dispose a mod service scope. Remove the host-held settings items and factory references before
the activation service scope retires; see [Service Boundary](07-ServiceBoundary.md).

The chat tool page reads ChatPlugin.SettingsItems for both built-in and mod-supplied tools.
Override that property to expose a tool's own settings. Everything supplies its two generated items
through EverythingChatPlugin.SettingsItems, sharing the same model with its engine and queries.
No mod-wide registration or tool-to-mod-ID association is needed for this entry.

Removing the selected tool clears selection and detail tabs. The settings tab also clears its
observable items property, so a temporarily retained template cannot keep the mod's bindings alive.
Closing or switching this editor does not dispose the model or its persistence engine.

## Optional mod-wide settings registration

The application provides IModSettingsRegistry, resolved through IModContext.Services.
The application contract is:

```csharp
public interface IModSettingsRegistry
{
    IAsyncDisposable Register(IModContext context, Func<SettingsItems> createItems);
    Control? CreateView(string modId);
}
```

ModSettingsRegistry implements this interface and is registered in the application service provider.
Registration is associated with the supplied activation, not just its reusable ModId. Accept one settings entry per activation; use existing SettingsItems groups
within that entry. Duplicate live registrations for the same mod ID are rejected. CreateView is the
UI-thread consumer API: it returns a stable host SettingsItemsControl or null when no usable registration
exists. It does not activate a mod. Registration returns an idempotent reversible handle. The mod retains it and registers
that responsibility with its context, following the engine's existing ownership rules.

The future management page uses a mod list on the left and details, README and optional settings
on the right, following the interaction pattern of ChatPluginPage. Persistence does not depend on
this page being open. This registry is not consumed by the chat tool page. Registration does not
depend on providing chat tools or using SettingsEngine. The mod-wide editor may present shared
settings or a different selection of settings from its tool-specific editors.

Invoke the factory on the UI thread when the settings view is actually needed. Keep presentation
objects and bindings stable while displayed. Reversal prevents further factory calls, removes the
contribution and detaches its view/bindings, then drops references to the factory and items. All UI
mutation is dispatched at the application boundary. An old activation's page cannot attach to a
replacement activation merely because their IDs match.

Disabled, skipped or unloaded mods can still show package metadata and README without executing
mod code. The first implementation does not offer their typed settings editor or activate them
solely to create one. Editing such settings would require a separate static-description design.

IHaveSettingsItems remains a model/presentation convenience; it does not itself publish a display
entry or establish activation ownership. Framework caches and custom controls retain the existing Avalonia
unload limits. Closing a page is necessary cleanup, not proof of assembly collection.

## Lifetime and final saving

Persistence does not prescribe when a preference affects runtime work. Each mod documents that
boundary and coordinates model access with the UI thread and any active operations.

Everything captures ExecutablePath and InstanceName together on the UI thread at query start.
Connection attempts use that pair; edits take effect on the next query without interrupting active
work or stopping user-owned processes. Its new settings document starts at format 1.0.0.

The mod entry owns the settings engine and awaits final saving in its normal disposal path.
IModContext.Register alone is insufficient: ordinary process exit skips the reversal queue.
An optional mod-wide settings registration is a reversible contribution, not the owner of persistence.

On hot stop, prevent further edits and complete UI detachment before final settings flush. Stop and
join mod work that can mutate settings, stop observation, flush pending changes and release the
engine. Coordinate early page closure with its registered reversal so each responsibility is
released once or through an explicitly idempotent handle. Entry disposal precedes registered
reversal in the existing engine; simply waiting for that later reversal would be too late to prevent
edits during final saving. When a mod-wide page is registered, the entry awaits its retained handle
before disposing resources used by that page; later context reversal safely awaits the same completed
handle. Tool-specific settings follow their chat registration and page lifetime instead. Completing a chat
registration's disposal drains admitted tool calls, not pending UI delivery. Coordinate the editor's
UI detachment before final saving; do not introduce a dispatcher wait into ordinary process exit.

Everything removes its chat registration on the UI thread during hot retirement so the chat page
detaches settings before final saving. Exit removes registration without a dispatcher wait.

Ordinary exit still saves settings, but cannot require a live UI dispatcher, new root-service
resolution or execution of the reversal queue. Hot-stop UI coordination must not introduce a UI
wait into the ordinary-exit path. IModContext.StopReason is null while running, then set to
ModStopReason.Unload or Exit before Stopping is signaled. Unload also covers initialization-failure
cleanup; an in-progress stop retains its original reason if process exit joins it. During Exit, the UI
handle removes the registration and drops its references without dispatching view mutations. The entry
can therefore await the same handle on both paths. Process exit does not promise collection of views
still owned by the terminating UI. Forced termination cannot guarantee a final save.

The storage serializes background and explicit writes. Final FlushAsync joins an in-flight write even
if its snapshot already consumed the dirty flag, and reports exhausted write failures. Engine disposal
stops observation and joins background persistence even when final saving fails.

Completed initialization failure uses the same resource ownership rules for whatever was created.
A settings failure must not silently replace existing data with defaults. Diagnostics retained by
the host after cleanup must not retain mod exception objects, types or migration delegates.

## Cache and JSON metadata lifetime

Use ConditionalWeakTable for a shared cache whose key is Type and whose value is a reusable,
type-only dictionary-key reader delegate. The value may refer back to its key; that association must
not alone keep a collectible type alive. A normal dictionary of weak keys with strongly held values
is not an equivalent replacement.

Pass the current JsonSerializerOptions into the cached reader on each call. The cached value must
not capture an engine's options, converters, model or services. A permanently live key such as
System.Int32 would otherwise retain mod converters even in a weak-key table.

Engine-specific descriptor and serialization state stays with that engine. Existing instance caches
may remain strong when their owner is released with the activation; weak references are not required
everywhere. Do not inject mod metadata into a global default resolver or use it as an implicit
fallback for mod types. Preserve the existing conversion semantics while removing static
application-options assumptions from reader and generated-descriptor paths.

These changes remove host-owned roots introduced by this channel. They do not promise that arbitrary
converters or framework caches are collectible. Validate the whole serialization path and retain
RestartRequired when collection is unconfirmed. ConditionalWeakTable's value-to-key behavior is
specified by [the .NET API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.conditionalweaktable-2#remarks).

## Migration declarations and versions

Reuse SettingsMigration, ISettingsMigrationMetadata and AddSettingsMigration<T>. A mod supplies
its own explicit migration registrations to a temporary initialization container. Never add those
registrations to the application root provider or discover all migrations across loaded assemblies.

Declare the current settings format version explicitly. It is independent of the package, host and
ModEngine versions. Retain the existing semantic-version representation and document Version
field; Version records settings format progress, not the running application version. For both the
application and mods, this field is document metadata, not a runtime model property. The engine
excludes it from model binding while preserving it in storage during normal saves. Pass the target
version to OpenSettingsAsync; a mod model property mapped to the JSON name Version is rejected.

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
blocks this mod's settings initialization; it is not the platform Skipped state.

## Migration execution and disposal

The following target-version policy and cleanup-before-commit sequence apply to the mod
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
the long-lived settings engine after migration completes. They legitimately hold mod types while
executing; deterministic ownership, not weak service references, ends that lifetime.

Migration, validation or temporary-resource cleanup failure before saving leaves the original file
unchanged and fails settings initialization. The temporary provider is disposed on both success and
failure, using ordinary MSDI ownership and DisposeAsync behavior. MSDI stops its disposal loop if a
service throws, so remaining services are not guaranteed a disposal callback; cleanup failure does
not authorize a retry of that disposed provider or a commit. Migration resources should finish their
own cleanup reliably. The engine does not introduce a replacement container to aggregate disposal
failures. Saving failures are
reported without falling back to defaults; no transaction with external files or databases is implied.

Once migration has been saved, later mod initialization failure does not roll it back. A retry
reads the new version and skips completed migrations. Hot unload and ordinary exit do not rerun or
reverse migrations. Selecting older mod code does not restore an older settings document.
Backups provide an explicit recovery option, not automatic activation rollback.

The standard guarantee covers transformation of this settings document. Avoid starting work,
publishing contributions or modifying external state in a migration. Auxiliary reads are possible,
but external writes and their recovery are outside the settings transaction.

## Implementation and validation closure

| Area | Current status |
|---|---|
| Persistence | Generic engine and application adapter share observation, patching and storage; mods own final saving |
| JSON and generation | Per-engine options and descriptors; explicit mod provider attribute and analyzer reference; SDK has no default settings generator |
| Caches | Weak type-only dictionary-key cache; options passed on every call; instance-owned descriptor metadata |
| UI | Tool-specific settings use ChatPlugin.SettingsItems; optional mod-wide registry remains separate; both have UI cleanup; mod management page deferred |
| Migrations | Explicit temporary registrations, target-version policy, backup/validation, asynchronous cleanup before commit implemented |
| Validation | Real collectible fixtures cover isolation, generated settings edits, key converters, retained closed handles, retry and exit; migration failures and version boundaries covered |

The maintained settings UI fixture uses GeneratedSettingsItems and its compiled bindings. A reflection
Binding comparison retained its collectible assembly; its exact framework root has not been diagnosed.
This is not a promise that arbitrary reflection bindings, custom controls or Avalonia caches unload.
Use confirmed engine collection and retain RestartRequired when collection fails.

Before final merge, review the public signatures, verify the chat tool page's actual desktop presentation,
and keep generated descriptor/serializer behavior consistent with application settings.
The management page and general Avalonia cache cleanup remain unimplemented. The application consumes
settings in the chat tool page and calls ApplyAsync only at startup. Before adding a hot-reload entry
point, coordinate outstanding UI retirement before dispatcher shutdown. The ordinary Exit test covers
disposal beginning as Exit, not exit racing an already-running hot stop.
