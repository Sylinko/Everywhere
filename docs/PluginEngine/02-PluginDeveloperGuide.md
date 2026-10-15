# Plugin Developer Guide

Read [Specification](01-Specification.md) for the runtime contract and [Validation](03-Validation.md) for tested capabilities. This guide distinguishes existing repository APIs from the SDK and package interfaces that have not been released.

**Design status (2026-10-13):** the SDK workflow, host imports and platform-entry model are agreed. The rewrite now contains buildable contracts, runtime and basic SDK tooling, but no SDK release or complete application integration. Implementation can reveal problems that require changing the design, including revising these instructions substantially. Before final merge, examples, supported capabilities and validation must match the implemented behavior; unresolved work must be completed or explicitly deferred with its limitations documented.

| Status | What authors can rely on today |
|---|---|
| Implemented in the rewrite | Selected-file validation, lifecycle/context registration, coordinated replacement and collection checks |
| Implemented and tested with collectible fixtures | Isolated optional methods and derived types in one DLL, controlled Assembly.Load(name), and rebuilding consumers when dependencies change |
| Basic implementation; broader validation pending | Host imports, platform entries, RequiresPlugin and the Plugins catalog; simple generated manifests and ZIPs |
| Implemented and Windows-tested | Platform `Skipped` status; preserved native/content output paths; on-demand native resolver and host-acquired handle release |
| Implemented | Optional IDisposable/IAsyncDisposable on plugin entries; normal DI exit releases entries and skips registered reversal |
| Implementation closure required | Complete schema/layout, SDK release, remaining dependency-selection policies and platform validation |

The previous runtime was deliberately removed and is not the implementation baseline. Registration, catalog and optional entry-disposal examples use the replacement public contracts. Sections marked pending still describe intended tooling or release behavior; the current SDK does not yet implement all declarations, analyzers or package layouts shown here.

## 1. What a plugin can do

Plugins are trusted code inside the Everywhere process. They can use real host types, provide host-facing objects, call other plugins, and use supported reflection or patching techniques. The loader does not sandbox them and cannot automatically undo arbitrary changes.

A plugin should support stopping and unloading wherever practical. Hot unload is conditional on successful shutdown, resource release and actual collection. A plugin that cannot complete those steps requires a process restart. An invalid dependency set must be corrected; restarting does not repair it.

The first release is intended to support ordinary managed plugins and validated non-generic method hooks. Generic method patching is a separate advanced capability, not currently an available official API.

## 2. SDK and starting a project

`Everywhere.Plugin.Abstractions.dll` contains lifecycle/context contracts and attributes, referencing the BCL without host implementation, UI or patch-library dependencies. `Everywhere.Plugin.Sdk` contains initial host-import and packaging tools; its complete authoring checks are still pending. `Everywhere.Plugin.Packaging` supplies manifest, host-reference and compatibility rules shared by the SDK and host. `Everywhere.Plugin.Hosting` handles source discovery, installation, managed loading and lifecycle. The SDK references Packaging and Abstractions, with no Hosting dependency. The abstraction assembly is host-owned; plugins must not ship another runtime copy.

Plugin authors implement `IPlugin` from Abstractions and use the SDK at build time. A normal plugin does not need to reference Hosting or Packaging explicitly. Host implementation APIs remain available through the host-import workflow when needed. The application imports `Build.Plugin.Hosting.targets` to export `plugin-host.json`; that is host build integration, not a targets file for plugin authors to import directly. See the [project dependency graph](01-Specification.md#components-and-version-boundaries).

The SDK will be distributed through NuGet as an MSBuild SDK, using the form `<Project Sdk="Everywhere.Plugin.Sdk/<sdk-version>">`. Its tooling version is independent of the public Plugin API version and Everywhere's application version. Several Everywhere releases may implement the same Plugin API. A tooling update alone is not a reason to reject a plugin at runtime. No SDK release number or installation command is available yet.

### Choose a host input

The first SDK will support two workflows through the same reference-import and packaging pipeline:

| Input | Intended workflow |
|---|---|
| An installed or extracted Everywhere application | Supply the application directory, or the macOS `.app` root. The SDK reads its declared references, host/platform identity and available XML documentation. |
| An Everywhere source project | Supply the relevant `.csproj`. The SDK builds/exports the required reference set and applies the same ownership and packaging checks. |

These workflows start with the first plugin-enabled Everywhere release. Older installations will receive a diagnostic; the SDK will not guess the host contract by collecting every DLL in their directories. A separate NuGet distribution of Everywhere host references is not required. References to third-party libraries and other plugins can still use ordinary NuGet packages.

The host's `.deps.json`, runtime configuration and small amount of plugin-specific host metadata identify its declared files and roles. The SDK reads the listed assemblies' identities and references. It does not discover host ownership through filename prefixes or signing keys, and authors do not maintain a second host manifest.

Host assemblies, including host-provided third-party libraries, stay out of your plugin payload. If you reference a NuGet library also provided by the host, tooling checks compatibility and shares the host copy or reports the conflict. Merely setting `Private=false` on a ProjectReference is insufficient because transitive host assemblies can still be copied. The SDK must inspect the resolved references and final package.

### Keep local paths outside shared project configuration

Use an ignored `Everywhere.local.props` file, with a tracked example showing the required settings. The current SDK imports this file and accepts `EverywhereHost` as an installation directory or source-project path. Build properties supplied by CI can override the local path. Relative paths are valid, and distributed plugin metadata must not contain machine-specific absolute paths. The [navigation sample](../../plugins/samples/Everywhere.Plugin.Sample/README.md) shows the implemented workspace build and packaging commands.

For example, the local file can associate a Windows host property with an installation directory and a macOS host property with an extracted `.app` or source project. Shared project configuration refers to those properties instead of hard-coding one developer's machine. Exact property and item names will be published with the SDK; do not treat illustrative names as usable commands today.

### Build and test the requested target

Host-reference import does not run Everywhere or load its native libraries. A Windows developer can supply an extracted macOS application as a reference input. Source import may still require target-platform SDKs or workloads. The SDK should avoid unrelated packaging, signing and NativeAOT helper publishing when only managed references are needed, but it must not silently change target frameworks or omit required functionality.

Reference import, managed compilation, native builds, application bundling/signing and target-platform execution are separate capabilities. The SDK is intended to support cross-compilation where the toolchain permits it; the complete Everywhere matrix has not been validated. Having the source checkout does not automatically make every platform project buildable on every OS.

For multi-platform plugins, provide a host input for each requested target. Missing macOS inputs should not block an explicitly Windows-only build. Packaging a plugin that declares both Windows and macOS entries requires both artifacts; it must not silently omit one. See section 5 for project organization.

The intended author/agent workflow is to create a project, configure local host inputs, write source and project declarations, build the requested entries, validate/package generated metadata and test against the selected host. Authors and coding agents never hand-write or repair generated JSON. Templates, CLI commands and analyzer configuration still need implementation.

XML documentation will be generated/retained with host references where available. `PreserveCompilationContext` is not a prerequisite for installation imports and does not need to ship a full `refs` directory. The maintained SDK tests cover ordinary directory and source imports; published cross-platform builds remain a release gate.

The runtime project is `src/Everywhere.Plugin.Hosting/Everywhere.Plugin.Hosting.csproj`. Plugin authors implement `Everywhere.Plugin.Abstractions.IPlugin`. Retained historical lifecycle probes still require migration to the replacement API before their results can validate this rewrite.

### Access host internals when needed

For trusted plugins that need internal host APIs, [IgnoresAccessChecksToGenerator](https://github.com/aelij/IgnoresAccessChecksToGenerator) is the recommended opt-in build tool. The repository already uses it for patch projects. Declare the specific assembly names you need:

```xml
<ItemGroup>
  <PackageReference Include="IgnoresAccessChecksToGenerator" Version="1.0.0" PrivateAssets="all" />
  <IgnoresAccessChecksTo Include="Everywhere.Core" />
</ItemGroup>
```

It generates compilation references exposing internal members and adds the runtime access-check attribute to the caller. The host DLL is not modified. Generated compilation references must never be distributed as replacement runtime DLLs. Internal APIs remain host implementation APIs, so declare and test the appropriate host version range. The upstream tool requires CompileUsingReferenceAssemblies=false when operating on ordinary ProjectReference inputs; the Everywhere SDK normalizes host exports into references, and both import workflows still need generator integration tests.

Access checks and inheritance restrictions are different. Ignoring access checks can permit use of an internal non-sealed base class; it does not permit deriving from a sealed class or overriding a non-virtual method. Use composition or an existing interface when it fits. If inheritance is an intended host extension point, that host class needs a deliberate source-level change and appropriate overridable members. A runtime method hook changes execution; it does not turn a class into a legal base class.

## 3. Lifecycle API

Register the disposable handles that reverse changes your plugin makes to the application. During hot
unload, the engine disposes those handles to remove the changes. Independently, an entry that owns
resources may implement IDisposable or IAsyncDisposable to perform its normal final cleanup, including
when Everywhere exits. Entries without final cleanup need neither disposal interface.

**Implemented:** IPlugin requires only InitializeAsync. The engine discovers optional disposal
interfaces on the entry instance, prefers IAsyncDisposable when both exist and never calls both or
falls back to Dispose after an asynchronous disposal failure. It also disposes a constructed instance
after initialization completes with an error. No disposal interface is needed for an entry that only
registers reversible contributions and owns no resources needing final cleanup.

Use a public, concrete, non-generic entry class with a public parameterless constructor. Keep constructors
free of subscriptions, background work and patches: a failed constructor cannot return an instance for
final disposal. Initialization may fail partway through, so cleanup must tolerate partially acquired state.

InitializeAsync receives an IPluginContext with these existing members:

| Member | Responsibility |
|---|---|
| PluginId | Logical identity of the activation |
| Services | Host services used during normal operation; not a promise that services can be resolved during container disposal |
| Stopping | Signal to stop accepting work and finish outstanding operations |
| Register | Register a disposable handle that reverses an application change made by this activation |
| Plugins | Activation-relative availability and host-owned plugin information |
| InstallationDirectory | Installed package files, read-only by contract |
| DataDirectory | Persistent data owned by this plugin ID |

The lifecycle follows ordinary object ownership. Keep short-lived files inside using or await
using scopes. Retain ongoing work so you can stop and await it before closing the resources it uses.
Flush buffered output and release connections in the entry's normal disposal, not only in registered
change-reversal handles. Prefer asynchronous disposal when the entry implements both interfaces; it
is one finalization responsibility, not two callbacks. Finalization also applies when an instance is
retired by hot unload or after completed initialization failure, not only when the process exits.

An entry declared as IPlugin, IAsyncDisposable can release its owned StreamWriter as follows.
This method assumes there is no concurrent writer; background work must be joined first.

```csharp
private StreamWriter? _writer;

/// <inheritdoc />
public async ValueTask DisposeAsync()
{
    var writer = _writer;
    _writer = null;

    if (writer is not null)
    {
        await writer.DisposeAsync().ConfigureAwait(false);
    }
}
```

The writer is ordinary owned IO: it is not registered with context.Register. A plugin with background
writers must stop and await them before this step. Keep important data saved during normal operation;
final disposal cannot guarantee persistence during forced termination or OS shutdown. Do not fetch UI
state at the last moment or depend on a running dispatcher during ordinary process exit.

Hot unload closes activation registration, signals Stopping, awaits entry disposal, and then reverses
registered changes. Assembly registrations are reversed when their owners retire, before unloading and
collection checks. Keep the state needed by removal handles usable after entry disposal. A patch or
subscription may still be installed while the entry is disposing: reject new work and await existing
callbacks before releasing what they use. The engine does not track those callbacks for you.

Ordinary application exit uses DI disposal after the UI lifetime ends. It signals Stopping and awaits
entry disposal, but skips both reversal containers, assembly unloading and collection checks. Do not
resolve new host services or await the UI dispatcher in this path. Fast-close means skipping hot-unload
work, not a five-second deadline; a disposer that does not finish can delay exit. Plugin lifecycle and
reversal callbacks must not recursively apply plugin changes or dispose the engine.

For navigation contributions, create your IMainViewNavigationTopLevelItem on the UI thread and call
MainViewModel.RegisterNavigationItem. Register the returned removal handle with context.Register; its
asynchronous disposal dispatches removal to the UI thread. Register the LocaleManager.RegisterProvider
handle before creating localized controls. Use the existing I18N generator and dynamic keys. The
[sample](../../plugins/samples/Everywhere.Plugin.Sample/README.md) demonstrates these registrations with
compiled AXAML and RESX. Its entry needs no disposal interface because its contributions are registered reversals.
For local validation, put the generated ZIP directly in `~/.everywhere/plugins`, or put an unpacked package folder there with `plugin.json` directly inside it. Keep one source per plugin. Start the newly built plugin-enabled application. Startup imports immutable runtime copies into the application data directory and reuses unchanged imports. Source additions, replacements and removal take effect on the next launch; no management page or live watcher is provided. On Windows, installation copies live in `%APPDATA%/Everywhere/installed-plugins`, temporary imports in `%APPDATA%/Everywhere/cache/plugin-staging`, and persistent plugin data remains under `~/.everywhere/plugins-data/{id}`. Do not edit generated runtime copies. The initial UI sample passed headless behavior and cleanup checks but returned `RestartRequired` on collection verification, so it does not establish support for hot-unloading Avalonia controls.

## 4. Owning resources and side effects

### Register application changes, own ordinary resources

Every change to shared application state that must disappear when your plugin is removed needs an
owned reversal registered with the engine. A host API may return a disposable registration handle or
explicitly register it for you; do not register the same reversal twice. Examples include event
subscriptions, navigation contributions, patches and class-level framework registrations. Registration
does not inspect the object or infer what to undo: that behavior must be implemented by its disposer.

Keep context.Register and PluginResource.Register as the names. They accept resource objects rather
than Action callbacks. The actual parameter is object so one method accepts IDisposable and
IAsyncDisposable without ambiguous overloads for objects implementing both. Invalid objects are
rejected. Registration is synchronous, so there is no RegisterAsync method. When invoked, disposal
prefers IAsyncDisposable and calls only one interface.

| Object | Ownership and action |
|---|---|
| Navigation-registration handle | Register it; its disposal removes this contribution during hot unload |
| Method-hook handle | Register it; its disposal removes this patch during hot unload |
| File reader used by one operation | Use a local using/await using scope; no engine registration is needed |
| Writer or connection owned by the entry | Release it in the entry's own final cleanup; registration is not an exit callback |
| Plugin entry itself | Do not register it; entry finalization is a separate responsibility |

For example, these statements register the removal handle, not the page as an arbitrary object:

```csharp
var registration = navigation.RegisterNavigationItem(page);
context.Register(registration);
```

This fragment runs on the UI thread, with navigation and page already supplied by the plugin. The
returned handle's disposal reverses the navigation change. A file writer used by the same plugin stays
under normal object ownership. Do not register its final save as if saving were reversal of a host change.
Persistent plugin data is meant to survive unload; external writes and requests are not automatically
rolled back by the engine.

### When reversal and disposal touch the same resource

The distinction describes responsibilities, not mutually exclusive operations. Removing a preview
feature can also release its session, while application exit may need to release that session without
removing the feature from a UI that is already closing. Use one owner for the session and route both
paths through its release operation. The engine's preference for DisposeAsync over Dispose only chooses
an interface on one object; it cannot detect shared resources hidden inside different handles.

For a synchronous session, with work already drained and both calls serialized, the owner can use:

```csharp
private void UndoPreview()
{
    try
    {
        _previewRegistration.Dispose();
    }
    finally
    {
        ReleasePreview();
    }
}

public void Dispose()
{
    ReleasePreview();
}

private void ReleasePreview()
{
    var session = _previewSession;
    _previewSession = null;

    session?.Dispose();
}
```

Here a registered removal handle invokes UndoPreview; the entry implements IDisposable. Hot unload
disposes the entry first and later invokes the handle, so the session is released once. Exit disposes
the entry and skips the handle. The registration's removal must not require a live preview session.
If removal does need that session, the entry must perform the necessary detachment before closing it,
and the registered handle must tolerate that prior detachment. The engine does not reorder arbitrary
plugin actions or choose a different entry-disposal order for each plugin.

This is an ownership sketch, not a thread-safe asynchronous disposal template. When asynchronous release
can overlap, callers must await the same completion task. A boolean meaning "release started" is not
proof that release finished. Do not assume every undo action is repeatable: restoring an earlier setting
twice could overwrite another plugin's later change. Coordinate shared resource release without repeating
the whole reversal. A caller-created PluginResource can own ordinary resources, but each handle must
still have one owner; putting the same resource into two containers does not coordinate their disposal.

### Choose the owner of the registration

Use context.Register(resource) for changes belonging to this activation. The engine reverses them
when that instance is retired, even if a shared library remains loaded for another plugin.

Use static registration for changes belonging to a loaded assembly, including those made before
InitializeAsync receives a context:

```csharp
static MyControl()
{
    PluginResource.Register<MyControl>(CreateClassSubscription());
}
```

CreateClassSubscription represents an acquisition method returning a disposable subscription whose
disposer removes that subscription. Equivalent forms are PluginResource.Register(typeof(MyControl),
resource) and PluginResource.Register(typeof(MyControl).Assembly, resource). Use a type defined in the
intended plugin library, not List<MyControl>: the defining assembly determines ownership, not generic
arguments or the runtime type of the returned handle. No fallback container is created for host or
independently loaded assemblies.

If A and B share a library, its class-level registrations must survive removal of A while B still uses
it. They belong to that library's loading instance, not whichever plugin first ran its static constructor.
When the library is retired, its registrations are reversed before assembly release and collection
checks. Accepted registrations made before a completed construction/initialization failure are cleaned
when their owners retire. A failed static constructor needs a fresh loaded type before retry.

### Registration and disposal rules

Register each reversal immediately and once. Successful registration transfers responsibility for
that handle's eventual reversal to its owner container. Do not separately dispose it unless the handle
explicitly supports repeated disposal. Rejected handles remain yours to release. Neither the context
nor static registration makes active work safe to interrupt or converts an irreversible change into a
reversible one. Missing reversal means hot unload cannot be promised, not that exit must undo the change.

Activation registration closes when that activation starts stopping. Assembly registration closes when
that assembly is retired, allowing shared registrations to outlive individual activations. Containers
dispose sequentially in reverse registration order; this is not one global order across all containers.
They attempt remaining handles after exceptions and aggregate failures. Failure or timeout prevents
successful-unload confirmation. A timeout does not terminate code or permit concurrent destruction of
resources still in use. UI-bound handles must marshal their own reversal to the UI thread.

Ordinary process exit does not drain either engine-managed registration container. This does not mean
plugin-owned files or connections have no cleanup: they use the optional entry-disposal contract in
section 3. Installed callbacks may still be reached before termination and
must tolerate their owner having stopped, without starting new work or touching already released state.
Static registration provides no extra process-exit callback for a shared file writer or other static IO
object; such an object needs an explicit ordinary owner.

A manually created new PluginResource() is a resource container owned by its caller. Its DisposeAsync
really disposes the accepted objects; the engine's process-exit policy does not disable that operation.
Call Close if registration must stop before users drain, then await the first DisposeAsync operation.
Repeated calls do not repeat cleanup or wait for an earlier in-flight call. This general container
behavior does not turn static registration into application-exit resource management.

### Avalonia controls and framework registrations

**Framework integration remains under investigation.** PluginResource now provides assembly-owned
cleanup registration. The earlier full-type-scan/UnregisterByModule proposal is not the general solution:
it misses framework registrations and can force resolution of absent optional types. Automatic tracking
inside Avalonia is a candidate for unchanged third-party controls, not an implemented capability.
Authors still own ordinary subscriptions and UI contributions; the current sample requires restart
when collection is unconfirmed. Process exit skips registered-resource cleanup.

The navigation sample exposed property metadata caches holding its `SamplePage` type even though it defines no new Avalonia properties. An isolated probe removed those roots with the public API and confirmed both assembly and load-context collection. The current application path still reports `RestartRequired` until that cleanup is integrated. This result covers this sample, not all custom controls.

See [Known issues](03-Validation.md#known-issues) for the open limits affecting custom properties, AddOwner, class handlers, layout/render helpers, routed events and host-owned UI state. That chapter distinguishes reproduced failures, source-confirmed reference paths and untested components, and records current handling and the work needed to resolve each issue. These are known limits, not a blanket prohibition on custom controls.

For a class handler that exposes a token, register it during activation. For example, on the UI thread, with `PluginView` being your control type:

```csharp
context.Register(
    Control.IsVisibleProperty.Changed.AddClassHandler<PluginView>(
        static (view, _) => view.InvalidateVisual()));
```

This makes this particular subscription reversible; it does not remove unrelated registrations made by the control. A plugin-owned static field is not inherently an unload leak: the problematic path is a surviving host/framework object retaining a plugin type, delegate or instance. Do not clear shared framework tables through reflection or unregister host types as a generic workaround. Use the engine's collection result to determine whether the current process can safely continue changing plugins. Source-reviewed limits and experimentally confirmed behavior are separated in [Validation](03-Validation.md#avalonia-cleanup-evidence-and-limits).

## 5. Dependencies and conditional code

Multiple plugins can share types because the loader selects one assembly instance per managed simple name. A conflicting dependency version is rejected rather than hidden in a private copy with incompatible type identity. Host assemblies and the lifecycle contract must not be shipped as competing plugin-owned DLLs.

Active dependencies must form directed acyclic graphs. A -> B -> D and A -> C -> D is valid; A -> B -> A is not. A selected dependency loads and initializes first and stops last, including an optional dependency selected for this activation. Updating a shared library may require reloading all consumers.

Package version, AssemblyVersion and file hash describe different things. Tooling will record the exact build selection, the author-declared allowed runtime range and the combinations actually tested separately. Matching a range is not proof that an untested future version works. Defaults for other-plugin and library dependency ranges still need implementation decisions; the host-range policy is defined in section 6.

The runtime accepts selected files and explicit required/optional dependency declarations. It implements the catalog, conditional entry validation, controlled name-based binding and whole-consumer reconstruction below. Missing undeclared references are rejected. It does not select package versions, generate all optional dependency declarations from author projects or provide complete conditional type-discovery/analyzer tooling.

### Declare optional dependencies in the project — agreed, not implemented

Declare that your plugin can run without another plugin, rather than marking your whole plugin LazyLoad. The engine may still prepare available dependencies before your plugin to preserve load order and lifetime behavior. No promise is made to defer a DLL until its first call.

The agreed SDK authoring form is:

```xml
<PackageReference Include="Acme.Translator.Plugin"
                  Version="1.4.2"
                  PluginDependency="Optional"
                  PluginVersionRange="[1.4,2.0)" />
```

`PluginDependency` and `PluginVersionRange` are custom fields for the future SDK, not existing NuGet features. Version selects the package used to compile; PluginVersionRange declares which logical plugin versions you permit at runtime. Ordinary plugin references default to required. The SDK obtains the plugin ID and assembly mapping from the referenced plugin's build metadata. ProjectReference will support equivalent intent. Dynamic-only dependencies without a build reference will use an explicit PluginDependency project item; its detailed syntax is still reserved.

Do not write generated manifest JSON. Change the source/project declaration and rebuild. Defaults for other-plugin dependency ranges remain undecided, so these examples specify a range explicitly.

### Query whether another plugin is usable — implemented

`IPluginContext` exposes `IPluginCatalog Plugins { get; }` with these operations:

```csharp
public interface IPluginCatalog
{
    bool IsAvailable(string pluginId);

    bool TryGetInfo(string pluginId, [NotNullWhen(true)] out PluginInfo? info);
}
```

The signature uses NotNullWhen from System.Diagnostics.CodeAnalysis. The catalog returns information, not another plugin's lifecycle instance.

| Question | API / meaning |
|---|---|
| Can my plugin use B in this activation? | `Plugins.IsAvailable("B")`: B is a declared, compatible, selected dependency that finished initialization |
| Does the host know B? | `Plugins.TryGetInfo("B", out var info)`: no loading or enablement side effect |
| Did the user request B to be enabled? | `info.IsEnabled`: desired configuration, not successful initialization |
| What is B doing? | `info.State`: `Inactive`, `Initializing`, `Active`, `Stopping`, `Failed` or `Skipped` |

Use IsAvailable to guard calls. A plugin can be enabled but skipped for an unsupported platform or failed, or globally active but incompatible with your dependency range. Querying IsAvailable for an undeclared dependency is an error; use TryGetInfo for general information about arbitrary plugins. A declared optional dependency that is absent, disabled, platform-skipped or incompatible returns false. `TryGetInfo` exposes `SkipReason = UnsupportedPlatform`; skipping does not satisfy a required dependency. Querying an unknown ID with TryGetInfo returns false.

PluginInfo is an immutable snapshot containing identity, version and status information. It does not contain Assembly, Type, plugin instances, delegates or exception objects that could keep another plugin alive. A snapshot can become out of date and does not retain a dependency for you. There is no IsLoaded readiness flag: one plugin may have several assemblies, and a stopped plugin can still have uncollected assemblies. Physical residency belongs in engine diagnostics.

### Guard calls across a method boundary — runtime supported, analyzers pending

The following example assumes Acme.Translator provides TranslatorApi and is declared optional in the project. Everywhere.Plugin.Abstractions and RequiresPlugin exist; SDK project declaration generation remains pending:

```csharp
using System.Runtime.CompilerServices;
using Everywhere.Plugin.Abstractions;

public static class TranslationFeature
{
    public static string GetText(IPluginContext context)
    {
        if (!context.Plugins.IsAvailable("acme.translator")) return "Hello";
        return Translate();
    }

    [RequiresPlugin("acme.translator")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Translate()
    {
        return Acme.Translator.TranslatorApi.Translate("Hello");
    }
}
```

Each part has a different job: IsAvailable guards execution, NoInlining preserves the call boundary, and RequiresPlugin tells the SDK and loader which dependency the method requires. RequiresPlugin does not inject a check, change CLR behavior, or add NoInlining automatically. NoInlining does not prevent explicit reflection or pre-JIT from reaching the method.

All required plugin IDs must be declared in the project. Multiple RequiresPlugin attributes require every condition to be satisfied; they do not express alternatives. Source annotations do not override compatibility ranges.

### Inherit from an optional plugin's type — runtime supported, discovery tooling pending

An optional derived type can stay in your ordinary entry DLL. A separate integration DLL is useful if you need a separate removal boundary, but is not mandatory. For example, assuming the dependency supplies PanelBase with a parameterless constructor and a Render method:

```csharp
[RequiresPlugin("acme.translator")]
public sealed class TranslationPanel : Acme.Translator.PanelBase
{
}

public static class OptionalPanelFeature
{
    public static void Render(IPluginContext context)
    {
        if (!context.Plugins.IsAvailable("acme.translator")) return;
        RenderPanel();
    }

    [RequiresPlugin("acme.translator")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RenderPanel()
    {
        var panel = new TranslationPanel();
        panel.Render();
    }
}
```

This example uses the same imports as the previous one. Real registered panels need lifetime cleanup under section 4. The loader must read the condition from PE metadata and skip TranslationPanel before requesting its runtime Type when the dependency is unavailable. A type-level condition covers its members and nested types; additional member conditions are cumulative.

Keep the ordinary entrypoint and unconditional code free of optional base classes, interfaces, field types, signatures and generic constraints. Do not enumerate the entire assembly with GetTypes and expect to filter afterwards. Third-party scanners may also cross this boundary. Planned SDK checks must cover common mistakes and generated code from async methods/lambdas, but a complete analyzer is not implemented and cannot prove arbitrary reflection safe.

### What happens when the dependency changes — implemented

Your available dependency set is chosen before InitializeAsync; selected dependencies initialize first. During normal execution, the host keeps that selection stable. If B is enabled, disabled or replaced, the engine can stop and reconstruct your entire plugin A. It also includes affected supporting libraries and consumers, while preserving unrelated plugins where possible. It does not change existing base types or detach bindings inside your surviving context.

After reconstruction, InitializeAsync receives a new context and checks the new availability. No dependency-change event is required for this first API. A previously absent optional dependency is still recorded in declarations so the engine knows that enabling it affects A.

When Stopping is signalled, reject new work and await outstanding calls before shutdown completes. The engine drains consumers before stopping their dependencies. IsAvailable is not a task tracker or a lifetime lease; retaining callbacks or failing to await work can still prevent unloading and require restart.

Optional does not mean ignoring initialization errors. If B was selected but fails initialization, the activation attempt fails and is cleaned up. The engine does not continue initializing A with an altered dependency set. A later explicit attempt without B is allowed only after cleanup and collection have been confirmed.

### Use declared name-based loading — agreed, not implemented as a full policy

The intended API is ordinary `Assembly.Load("Acme.Translator")`; no PluginContext.LoadAssembly wrapper is planned. Declare the dependency even if reflection is your only access path. The argument is an assembly name, not a plugin ID. Check plugin availability before using plugin APIs: obtaining an Assembly is not proof of successful initialization.

Calls entering the controlled resolver must return the selected instance and reject unavailable, undeclared or incompatible bindings. Calls made by collectible supporting libraries need declarations as well. Default-owned shared libraries may need the host to establish a scoped contextual-reflection environment; do not assume every Assembly.Load call in the process uses your plugin's resolver. Representative third-party libraries and async/concurrent propagation still need validation.

LoadFile, byte-array loading and custom loading contexts bypass this supported ownership path. If your code or a dependency uses them, the engine cannot promise coordinated unloading of those assemblies. Runtime-generated assemblies and expression compilation also need lifetime checks; neither automatic rejection nor guaranteed collection follows solely from their being dynamic.

The rewritten resolver enforces requester declarations and reconstructs consumers when optional availability changes. The current fixture tests exercise these transitions and controlled name-based binding. SDK dependency declaration generation and authoring analyzers remain pending. See [Validation](03-Validation.md#historical-findings-and-their-limits) for the limits of the historical experiments.

### Organize platform-specific projects — design agreed, tooling pending

A plugin has one ID and version and one enable/update/lifecycle identity. It declares one universal entry or several mutually exclusive platform entries. Each entry names its assembly and concrete IPlugin class; only one runs in a given activation. Supporting libraries need no entry class. Use separate plugin IDs if two features need independent lifecycles or updates.

An ordinary neutral plugin uses one project and a simple folder/archive. For platform APIs, a useful organization is:

```text
Acme.Clipboard/
  Plugin.props                  Shared plugin ID and version declarations
  Everywhere.local.props       Ignored machine-specific host locations
  Everywhere.local.props.example
  Common/                       Supporting code, no lifecycle entry required
  Windows/                      Windows entry project -> Common
  Mac/                          macOS entry project -> Common
```

These are illustrative project paths, not required package paths. Both platform projects build the same plugin identity/version. Common must not reference the platform entry projects. Shared source compiled once per target is another valid approach. Use normal target framework, RID and minimum-OS settings where possible; plugin-specific settings cover additional intent. Do not choose cross-target source files solely from the build machine's OS.

The Windows and macOS host projects both emit `Everywhere.dll`. Compile each plugin entry against its matching host set; do not add both host files to the same compilation. A neutral target framework alone does not prove that all APIs you use work on every OS.

The loader selects the entry and applicable common/platform assets before dependency validation. Unselected platform files do not create active dependencies. `OperatingSystem.IsWindows()` guards execution but does not remove an AssemblyRef. Keep unavailable platform dependencies in separately selected artifacts or conditional builds. RequiresPlugin concerns another plugin's availability; it does not replace platform selection.

The SDK preserves MSBuild output-relative paths, including satellite target paths. Resource-only satellites are identified by assembly metadata (name and culture), associated with their parent DLL, and loaded into that parent's collectible context. There is no hard-coded language list or language-folder discovery. ResourceManager provides normal parent-culture and neutral fallback. The SDK also preserves dependency records, native assets and executable/content files. Native resolution is on demand as described below; multi-entry aggregation and cross-platform native validation remain incomplete. Platform selection and archive installation APIs exist, while full application package management does not.

### Native libraries and executable content

Use the normal build/RID output. The SDK copies its declared output paths and generates separate
`assemblies`, `nativeLibraries` and `contentFiles` lists. You do not maintain these lists manually.
Set the entry platform to the actual supported architecture, such as `win-x64`, when shipping an
architecture-specific DLL. NuGet restore selects its runtime assets; the plugin loader does not
download packages or recreate NuGet restore at activation time. Keep the generated `.deps.json`.

A native DLL is resolved when P/Invoke or another supported request needs it. The host first uses
the dependency resolver for the selected package and then declared copied-file paths. It does not
preload every native DLL or inspect the whole native dependency tree. Unknown/system names return
to normal runtime probing. A library's own DllImport resolver keeps precedence; raw path loads,
OS calls and native transitive imports may bypass the host's tracking. Do not add an absolute-path
resolver merely to find a packaged DLL when the host already resolves it.

Sharing a managed wrapper also shares its native binding. Packages providing that wrapper must
agree on the requested library, including its bytes. A provider that has no matching asset cannot
borrow another package's private native DLL silently. Disagreement fails at the native request.
Provider changes can require reloading all consumers. Use a different wrapper assembly identity
when the bindings intentionally differ.

Stop native calls, unregister callbacks, join threads and dispose native clients before entry
disposal completes. The host releases only native loader references it acquired, after managed
collection has been confirmed. It keeps them when cleanup/collection fails and requires restart.
It cannot infer that a native thread is finished from managed collection alone. If you load a DLL
yourself, you own its lifetime and must validate whether release is safe.

Executables are content, not DLLs to preload. Launch only when needed and explicitly distinguish
user-owned from plugin-owned processes. Mutable configuration/databases belong in DataDirectory;
copy an executable there first if it writes beside itself. Native lifetime validation remains
specific to each library and platform.

### Register chat tools without retaining their implementation

Derive your tool group from PluginChatPlugin and populate it with NativeChatFunction instances.
BuiltInChatPlugin is reserved for application-owned tools; both share NativeChatPlugin. The manager
exposes them together through NativePlugins. A group named `everything` has Key `plugin.everything`.
Choose a stable group name that does not collide with another installed plugin's group. Key is used
by persisted settings and approval rules, independently of names deduplicated for the model.

Resolve `IChatPluginManager` from host services and call `RegisterChatPlugin(yourTool)`.
Register its returned IAsyncDisposable handle with the context. For an entry that owns ongoing
queries, signal its stop source, dispose the handle to revoke and drain calls, then dispose the
underlying connection/tool in entry disposal. The handle supports repeated disposal, so subsequent
hot-unload reversal is safe. This also releases queries on ordinary exit, when the general reversal
queue is deliberately skipped.

Discovery exposes your actual tool instance, including its settings, warnings and dynamic
`GetAvailableFunctionsAsync` implementation. Each new chat scope uses that discovery and the normal
enablement rules. Existing scopes retain their selected tools through weak, revocable function proxies.
Use those scopes for chat execution; retaining the real instance from the discovery list retains the plugin.

Closing the handle removes discovery, rejects new calls through old proxies and waits for admitted
calls, post-invocation filters and streaming enumerators to finish or be disposed. Close it before
disposing connections or the tool itself. A newly registered activation does not revive old proxies.
Permission and friendly-rendering callbacks are also revoked. UI contributions still need their own
removal/disposal; this registration does not make arbitrary controls or display objects collectible.

Your methods keep the normal SK execution behavior: complex parameter binding, optional defaults,
injected services, filters, cancellation and streaming. The host does not parse arguments, clone the
kernel, convert result values or replace exceptions. It only changes FunctionResult.Function to refer
to the proxy, so a stored result does not retain the original method through that property.

Pass callbacks directly to NativeChatFunction using `permissionConsentHook` and `beforeCallHook`:

- `PermissionConsentHook` receives FunctionCallContext and PermissionConsentRequest when existing
  rules have not bypassed approval. Return true to allow, false to deny, or null to continue normal
  approval. You may customize the request's HeaderKey, RememberMasks and CustomOptions. Obtain the
  original call from `context.FunctionCallContent`. Use ToolConsentOption.BypassPluginApproval for
  a host-handled option to remember approval for the current tool group; arbitrary option handlers
  are not part of outer approval. The host owns dialogs, automatic review and rule persistence.
- `BeforeCallHook` receives the original call before approval, even when Arguments is null or empty.
  Return a ChatPluginDisplayBlock to display, or null. The call may still be denied, so do not execute
  the tool operation here. Use host-owned display types and values that can outlive your plugin.

Both hooks are synchronous and do not imply UI-thread execution. The host forwards registered hooks
without retaining plugin delegates in chat snapshots and stops forwarding when registration closes.
Consent requests can stay alive while a user decides: their titles, options and values must not keep
plugin-defined objects alive. Existing FullAccess and bypass rules take precedence over the hook.
Returning null in Auto mode requests the existing automatic reviewer rather than opening a dialog.

The proxy exposes a persistent description, not the original CLR reflection surface. Names,
descriptions, required flags and SK-generated schemas remain available; ParameterType and
UnderlyingMethod do not. Serializer options are not copied into the description.
Parameter metadata's `DefaultValue` is assigned directly, without a type whitelist, serialization or
conversion. Reference-type defaults keep the original object reference, including mutable objects.
If a default is a plugin-owned object or contains references to plugin objects or types, a retained
chat snapshot can prevent unloading. You are responsible for these references; the host will not
erase defaults to force collection, and unconfirmed collection results in `RestartRequired`.
SK's ordinary method-derived defaults already use strings. Actual argument/default binding remains
SK's job; setting description metadata does not add an optional parameter to the underlying method.

Function metadata's `AdditionalProperties` is shallow-copied into a new host-owned dictionary for each
proxy and clone. All keys and values are preserved without filtering or serialization. Changing which
entries exist in the original dictionary does not change an existing snapshot, but objects stored as
values are still shared. Strings and Boolean flags do not themselves retain your plugin. Plugin-owned
objects, delegates, `Type` values and collections containing them can prevent unloading for as long as
the chat snapshot is retained. You are responsible for these references; the host will not erase chat
metadata to force collection. If collection cannot be confirmed, the result is `RestartRequired`.

For complex plugin-owned parameter types, you can pass `jsonSerializerOptions` to NativeChatFunction.
These options go directly to SK for schema generation and binding. For example:

```csharp
new NativeChatFunction(
    Search,
    ChatFunctionPermissions.FileRead,
    jsonSerializerOptions: SearchJsonContext.Default.Options);
```

Use your normal System.Text.Json source-generated context or converters; include metadata for the
parameter and return types SK needs. The host patches SK's strong parameter-converter cache to use
weak keys for collectible types, without changing conversion behavior.

Results remain the objects your method returns. Prefer host-owned result types, strings or a prebuilt
JsonElement when chat history must survive plugin removal. A plugin-defined result, exception or
metadata value held by history can still retain the plugin. Reflection-based serialization elsewhere
can also retain type/accessor caches; generated serialization avoids that particular path. Existing
chat persistence rules still apply. The proxy does not promise that every object graph can unload,
and the engine reports RestartRequired when collection cannot be confirmed.

Use host-owned locale keys and ordinary strings/numbers in persistent messages. Do not put a
plugin enum, Type or custom object into a DirectLocaleKey. FormattedDynamicLocaleKey and
AggregateDynamicLocaleKey copy the collection container, because a compiler-generated collection
inside a plugin can otherwise retain its assembly even when each element is a host-owned key.
That copy does not remove references inside plugin-defined elements.

### Distribution, installed files and plugin data

The SDK produces a standard ZIP. The host extracts it once into a staging directory, validates it, and moves the complete directory to a unique final installation path before activation. Package-relative paths are preserved. It loads from that directory, not from the ZIP or a newly extracted temporary copy at each launch. Startup accepts either a ZIP or an equivalent unpacked package folder under ~/.everywhere/plugins. The original source is the user-facing installation unit; keep it there. Unchanged sources reuse their imported copies.

Each installation gets its own directory, including rebuilds with the same plugin version. Treat package files as read-only while installed. Read bundled resources from `IPluginContext.InstallationDirectory` and write configuration/databases to `IPluginContext.DataDirectory`, the stable directory associated with your plugin ID. Updating or uninstalling code must not implicitly delete persistent data. Replace or remove original packages while the application is stopped. Source removal excludes the plugin on the next launch and preserves its data.

An imported package can remain inactive because of a dependency conflict. Startup import and reuse are implemented. Persisted disablement, restart handoff and automatic cleanup of superseded copies remain pending. The agreed update contract is as follows: updates preflight the desired set before stopping affected plugins. If old owners cannot be released, the host retains both installations and records the change for restart. New initialization failures use the ordinary cleanup rules; there is no automatic retry loop or automatic restoration of the old activation. Old files are removed only when no longer needed; deletion failure can be retried later without invalidating a successful update.

Keeping an older installation makes explicit code rollback possible, but it does not undo database migrations or external effects. Design data compatibility and recovery accordingly. See the [installation contract](01-Specification.md#package-installation-updates-and-persistent-data) for staging, persistence and cleanup rules.

## 6. Generated entries and compatibility

**Design agreed; complete schema/tooling pending.** You declare intent in projects and source; the SDK generates metadata from the resolved build inputs and final runtime files. The host rechecks declarations and actual files. Build locks and exact build versions support reproducibility; they do not automatically declare compatibility with other versions.

The agreed entry shape for one plugin with two platform implementations is shown below. This is an explanation of generated output, not a manifest to maintain by hand or a complete package schema:

```json
{
  "id": "acme.clipboard",
  "version": "1.0.0",
  "entry": {
    "$type": "perPlatform",
    "variants": [
      {
        "platform": "win",
        "assembly": "windows/Acme.Clipboard.Windows.dll",
        "type": "Acme.Clipboard.Windows.Plugin",
        "hostVersion": "[0.9.0,0.10.0)"
      },
      {
        "platform": "osx",
        "assembly": "macos/Acme.Clipboard.Mac.dll",
        "type": "Acme.Clipboard.Mac.Plugin",
        "hostVersion": "[0.9.2,0.10.0)"
      }
    ]
  }
}
```

A universal entry uses `"$type": "universal"`, with `assembly`, `type` and any applicable `hostVersion` directly on `entry`. A single-platform plugin uses `perPlatform` with one variant. `$type` identifies the metadata shape; `type` is the full entry class name. Paths are package-relative; the example directory names are not a frozen layout.

Platform names use portable .NET RIDs: `win`, `win-x64`, `osx`, `osx-arm64`. Use `osx`, not `macos`, for the platform value. `win` does not mean x64: it allows compatible Windows process architectures subject to the actual assets' constraints. If both `win-x64` and `win` entries exist, a Windows x64 process selects the specific one. Matching follows the RID graph, not a prefix or array position. Duplicate platform declarations and unresolved ambiguity are errors.

When no platform entry matches, the plugin stays enabled but has `State = Skipped` and `SkipReason = UnsupportedPlatform`; it executes no entry code. This is a per-plugin status, not an activation failure. Malformed metadata and incompatible selected entries still fail validation. The loader selects the platform entry first, then validates its compatibility. It does not fall back from an incompatible `win-x64` entry to `win` just because the latter permits the installed Everywhere version. Only the selected entry's dependencies and common applicable assets participate. Record the minimum OS/runtime separately from RID where needed; generic platform spelling cannot make architecture-specific native files portable.

### Declare compatibility for the entry you build

Your plugin has one exact version across its entries. The SDK version identifies build tooling; the Plugin API requirement describes the host-owned abstraction contract. Everywhere's version is a separate constraint when your entry uses implementation APIs. Multiple host releases can share one Plugin API version.

Host compatibility belongs to the concrete entry, so Windows and macOS entries may declare different `hostVersion` ranges. For entries that reference host implementation APIs, local development can omit this range while tooling records the imported build. Distribution packaging requires an explicit permitted range; tooling may help you specify the exact imported version as a starting point. A broad range does not prove future versions work. Contract-only plugins need Plugin API compatibility without an unnecessary host-release restriction.

The public Plugin API policy preserves backward compatibility within one major version. The SDK defaults a stable Plugin API 1.2.0 build to [1.2.0,2.0.0); set `EverywherePluginApiVersion` to override this with a tested range that includes the imported contract. Prerelease contracts default to an exact range. This API policy does not infer compatibility for internal host APIs or arbitrary NuGet packages.

If implementing separate development/package rules proves impractical, the accepted fallback is to require an explicit host range in development too. Any such change must appear in the actual SDK and these instructions before merge. Plugin API/dependency range defaults and their complete serialized form remain to be finalized; they must not silently mean unlimited compatibility.

SDK validation will cross-check dependency intent, RequiresPlugin annotations and final artifacts. Referenced metadata maps plugin IDs to package origins and assembly identities without making authors repeat them. Repair validation errors in source/project inputs and rebuild. Do not edit generated JSON to hide conflicts, widen ranges or add absolute host paths.

## 7. Patching and special cases

Patching is optional. Add MonoMod.RuntimeDetour to your plugin project only when your feature needs it; neither the Everywhere SDK nor the runtime includes it as a default dependency or exposes a special patch API. RuntimeDetour Hook is the starting point supported by the recorded non-generic experiments. Experiment results establish selected capabilities, not universal method support. Other patch libraries remain the plugin author's responsibility.

Register each owned hook with `context.Register(hook)` immediately after creation: its disposal reverses the patch. Stop accepting new plugin work and await in-flight callbacks before retiring their state. Entry-owned resource disposal is separate from handle reversal; see section 3 for optional entry disposal and callback-draining responsibilities. Process exit leaves registered hooks installed until termination. A stopped hook must therefore safely pass through or otherwise handle later host calls without starting new plugin work or accessing state already released by its owner. Do not dispose another plugin's handles, retain continuation delegates, or assume the engine automatically tracks patch callbacks. Hook registration uses the registered change-reversal contract; it is not a general process-exit resource queue.

The SDK handles the patch library and its dependencies through ordinary dependency rules. A shared library version must satisfy the selected plugin set. Earlier experiments loaded RuntimeDetour in Default; plugin-managed ownership is a separate deployment condition that still needs validation against the rewritten runtime. Do not interpret those experiments as a promise that every privately packaged patch library can unload.

| Situation | Guidance and boundary |
|---|---|
| Two plugins hook the same ordinary method | Use the library's explicit ordering and keep handles separately owned. Tested Hook plugins can be removed independently under the recorded deployment; verify the actual shared-library selection and collection as well. |
| Patch callback is running during shutdown | Stop accepting plugin work and await active callbacks before disposing resources. The loader cannot discover every task automatically. |
| Initialization fails after installing a hook | Registered cleanup plus partial-initialization-safe shutdown can remove it and permit a later retry. Unregistered mutations have no automatic recovery. |
| Callback passes straight through after shutdown | It may still be installed. Verify actual disposal separately from restored return values. |
| Shared generic type contains a plugin type | Let the loader prepare declared dependencies first. Tested cases unload independently; do not retain objects/types/delegates in host caches. Arbitrary combinations remain conditional. |
| Target method is generic, such as GetTypeName<T>() | The tested Hook entry rejects this case. Host extension points, caller/startup rewriting and profiler techniques need separate designs and tests; no supported recipe is published yet. |
| Method was inlined into callers | Patching a method does not establish that those callers are intercepted. Validate the real deployment and call paths. |
| Satellite resources | Supported for resource-only satellites declared by the SDK. Identity/culture come from metadata; resources share the parent DLL's context. |
| Native imports | Native resolution remains incomplete. Native lifetime rules must not be inferred from managed collection. |
| Dynamic loading outside the selected plan | Breaks the loader's ability to enforce its selected universe. Declare runtime assets where possible; restart remains the fallback for uncertain lifetime. |

Never repair unload problems by blindly clearing undocumented CLR or patch-library caches. Do not retain continuation delegates beyond their supported invocation lifetime.

## 8. Understanding operation results

| Result | What to do |
|---|---|
| Applied | The selected set was applied. Inspect individual snapshots: an enabled platform-specific plugin can be `Skipped`. |
| Rejected | Fix the package, dependency or compatibility input. No activation started. |
| ActivationFailed | The attempted activation failed, but new managed ownership was cleaned up and collected. Correct the cause and explicitly retry if appropriate. |
| RestartRequired | Do not assume the plugin is disabled or its patch is removed. Further changes are blocked pending the application's save/restart workflow. |

Retry does not mean automatically rerunning a failed callback. A deterministic bug normally fails again. During replacement, a failed new version does not automatically restore the stopped old version. Read the actual active/resident snapshot and diagnostic phase.

## 9. Testing and agent workflow — implementation pending

The future CLI should support creating projects, building packages, checking a specified host/plugin set and running lifecycle/behavior tests in isolated processes. Command names, test-host protocol and structured output schema are not yet available. A future Skill/MCP will reuse these operations.

Test successful initialization, partial initialization failure, shutdown failure/timeout, cleanup failure, repeat load/unload, retained host references and dependency replacement. For patches, check invocation counts/order, behavior before/during/after, and collection independently. Record exact host/runtime/dependency versions. Test the actual published app, not only a build output directory.

For optional code, test B absent, disabled, incompatible and available; exercise the actual optional method and derived type. Test enabling, disabling and replacing B after A has run, including same-AssemblyVersion replacements with different bytes. Confirm old callbacks and owners are released and unrelated plugins remain usable. Include negative controls for unfiltered type discovery, unguarded optional entrypoints and undeclared dynamic loads. Successful prototypes do not replace these checks against the eventual production SDK and engine.

For SDK and packaging, test both host-import workflows, transitive host-library exclusion, incompatible shared-package versions, XML documentation and reference refresh after host changes. For platform entries, test exact and fallback RID matches, unsupported platforms, duplicate entries and an incompatible specific entry that must not fall back. Verify that foreign-platform dependencies stay outside the active graph. Test the intended installation and source-build combinations separately; a successful managed build does not replace execution on the supported target OS.

The remaining schema, commands and build properties are implementation work, not instructions authors are expected to guess. Startup installation and reuse are implemented; the rest of the update policy and interrupted-operation tests remain pending. Native lifetime and advanced patching have separate design or validation work. The [specification's closure requirements](01-Specification.md#implementation-closure-and-final-merge) distinguish these from the completed SDK and installation architecture. Before final merge, finish all agreed in-scope work and reconcile the guide with working examples; explicitly agree and document any deferral. Implementation findings can require revising the design rather than preserving an earlier promise that cannot be supported.

Existing repository evidence and reproduction entrypoints are indexed in [Validation](03-Validation.md). Those disposable probe runners are maintainer tools, not the final plugin author CLI.

The optional-dependency and conditional-discovery design was inspired in part by [tModLoader's cross-mod mechanisms](https://github.com/tModLoader/tModLoader/wiki/Expert-Cross-Mod-Content). The names and lifecycle promises above are Everywhere's contract, not a compatibility layer for tModLoader mods. tModLoader is [MIT-licensed](https://github.com/tModLoader/tModLoader/blob/stable/LICENSE); retain applicable copyright and permission notices when reusing substantial covered code. The [specification's attribution](01-Specification.md#design-references-and-attribution) explains the scope of this reference.
