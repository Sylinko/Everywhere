# Mod Developer Guide

Read [Specification](01-Specification.md) for the runtime contract and [Validation](03-Validation.md) for tested capabilities. This guide distinguishes existing repository APIs from the SDK and package interfaces that have not been released.

**Design status (2026-10-13):** the SDK workflow, host imports and platform-entry model are agreed. The rewrite now contains buildable contracts, runtime and basic SDK tooling, but no SDK release or complete application integration. Implementation can reveal problems that require changing the design, including revising these instructions substantially. Before final merge, examples, supported capabilities and validation must match the implemented behavior; unresolved work must be completed or explicitly deferred with its limitations documented.

| Status | What authors can rely on today |
|---|---|
| Implemented in the rewrite | Selected-file validation, lifecycle/context registration, coordinated replacement and collection checks |
| Implemented and tested with collectible fixtures | Isolated optional methods and derived types in one DLL, controlled Assembly.Load(name), and rebuilding consumers when dependencies change |
| Basic implementation; broader validation pending | Host imports, platform entries, RequiresMod and the Mods catalog; simple generated manifests and ZIPs |
| Implemented and Windows-tested | Platform `Skipped` status; preserved native/content output paths; on-demand native resolver and host-acquired handle release |
| Implemented | Optional IDisposable/IAsyncDisposable on mod entries; normal DI exit releases entries and skips registered reversal |
| Implementation closure required | Complete schema/layout, SDK release, remaining dependency-selection policies and platform validation |

The previous runtime was deliberately removed and is not the implementation baseline. Registration, catalog and optional entry-disposal examples use the replacement public contracts. Sections marked pending still describe intended tooling or release behavior; the current SDK does not yet implement all declarations, analyzers or package layouts shown here.

## 1. What a mod can do

Mods are trusted code inside the Everywhere process. They can use real host types, provide host-facing objects, call other mods, and use supported reflection or patching techniques. The loader does not sandbox them and cannot automatically undo arbitrary changes.

A mod should support stopping and unloading wherever practical. Hot unload is conditional on successful shutdown, resource release and actual collection. A mod that cannot complete those steps requires a process restart. An invalid dependency set must be corrected; restarting does not repair it.

The first release is intended to support ordinary managed mods and validated non-generic method hooks. Generic method patching is a separate advanced capability, not currently an available official API.

## 2. SDK and starting a project

`Everywhere.Modding.Abstractions.dll` contains lifecycle/context contracts and attributes, referencing the BCL without host implementation, UI or patch-library dependencies. `Everywhere.Modding.Sdk` contains initial host-import and packaging tools; its complete authoring checks are still pending. `Everywhere.Modding.Packaging` supplies manifest, host-reference and compatibility rules shared by the SDK and host. `Everywhere.Modding.Hosting` handles source discovery, installation, managed loading and lifecycle. The SDK references Packaging and Abstractions, with no Hosting dependency. The abstraction assembly is host-owned; mods must not ship another runtime copy.

Mod authors implement `IMod` from Abstractions and use the SDK at build time. A normal mod does not need to reference Hosting or Packaging explicitly. Host implementation APIs remain available through the host-import workflow when needed. The application imports `Build.Modding.Hosting.targets` to export `mod-host.json`; that is host build integration, not a targets file for mod authors to import directly. See the [project dependency graph](01-Specification.md#components-and-version-boundaries).

The SDK will be distributed through NuGet as an MSBuild SDK, using the form `<Project Sdk="Everywhere.Modding.Sdk/<sdk-version>">`. Its tooling version is independent of the public Mod API version and Everywhere's application version. Several Everywhere releases may implement the same Mod API. A tooling update alone is not a reason to reject a mod at runtime. No SDK release number or installation command is available yet.

### Choose a host input

The SDK design supports two compilation workflows with shared host ownership, compatibility and packaging rules:

| Input | Intended workflow |
|---|---|
| An installed or extracted Everywhere application | A quick path for local development without a host checkout. Supply the application directory, or the macOS `.app` root. The SDK reads its declared runtime assemblies, host/platform identity and available XML documentation. |
| An Everywhere source project | The preferred path for advanced development against host source. Supply the mod-enabled host `.csproj`; the SDK uses standard ProjectReference resolution so MSBuild/NuGet select compilation assets and transitive dependencies, with the same host ownership and packaging checks. |

**Implemented and Windows-tested:** source imports use ProjectReference rather than importing runtime DLLs as compilation references. The selected host project and its implicit transitive project references keep their own identity/version, output paths, RID and deployment settings when the mod supplies command-line overrides. Configuration and Platform follow normal project-reference behavior; target frameworks must be compatible. The host descriptor is read from that same build. The SDK's internal `import` CLI accepts directories; source selection belongs to the MSBuild SDK through `EverywhereHost`.

Only NuGet compile assets flow through the host reference. If your mod needs a package's analyzers, source generators, build targets or runtime files of its own, declare that PackageReference explicitly. For example, an AXAML mod declares Avalonia itself. The SDK does not apply the host's build tooling to every mod. Host-owned project outputs and content are excluded, and the final package checks overlapping managed dependencies. This does not require a separate distribution of host reference assemblies.

These workflows start with the first mod-enabled Everywhere release. Older installations will receive a diagnostic; the SDK will not guess the host contract by collecting every DLL in their directories. A separate NuGet distribution of Everywhere host references is not required. References to third-party libraries and other mods can still use ordinary NuGet packages.

The host's `.deps.json`, runtime configuration and small amount of mod-specific host metadata identify its declared files and roles. The SDK reads the listed assemblies' identities and references. It does not discover host ownership through filename prefixes or signing keys, and authors do not maintain a second host manifest.

Host assemblies, including host-provided third-party libraries, stay out of your mod payload. If you reference a NuGet library also provided by the host, tooling checks compatibility and shares the host copy or reports the conflict. Merely setting `Private=false` on a ProjectReference is insufficient because transitive host assemblies can still be copied. The SDK must inspect the resolved references and final package.

### Known WindowsBase warning with runtime assembly imports

Importing a Windows host containing System.Reactive 7.0 can produce MSB3277 for WindowsBase versions 4.0.0.0 and 8.0.0.0. Rx's Windows runtime DLL retains legacy WPF/WinForms APIs for binary compatibility, while its NuGet `ref` assembly omits those APIs. Importing the runtime DLL as a compilation reference exposes its WindowsBase 8 dependency alongside the .NET reference pack's WindowsBase 4 identity. The latter assembly version does not mean the mod targets .NET Framework 4. See the [Rx package design](https://github.com/dotnet/reactive/blob/main/Rx.NET/Documentation/adr/0005-package-split.md).

This warning alone does not mean WindowsDesktop has been added to the deployed application. The validated host output contains only the Microsoft.NETCore.App runtime framework, and the packaged Everything mod passed real query and unload checks despite the warning. Using the legacy desktop APIs can still require additional framework references and cause compilation or runtime failures; this validation does not cover those APIs. Accept this known warning for the validated installation-import use case without suppressing it or adding a general workaround. The implemented ProjectReference path selects Rx's `ref` assembly; the Everything source-import build and package completed without this warning. This is not a promise that all dependencies are warning-free.

### Keep local paths outside shared project configuration

Use an ignored `Everywhere.local.props` file, with a tracked example showing the required settings. The current SDK imports this file and accepts `EverywhereHost` as an installation directory or source-project path. Build properties supplied by CI can override the local path. Relative paths are valid, and distributed mod metadata must not contain machine-specific absolute paths. The [navigation sample](../../mods/samples/Everywhere.Mods.Sample/README.md) shows the implemented workspace build and packaging commands.

For example, the local file can associate a Windows host property with an installation directory and a macOS host property with an extracted `.app` or source project. Shared project configuration refers to those properties instead of hard-coding one developer's machine. Exact property and item names will be published with the SDK; do not treat illustrative names as usable commands today.

### Build and test the requested target

Host-reference import does not run Everywhere or load its native libraries. A Windows developer can supply an extracted macOS application as a reference input. Source import may still require target-platform SDKs or workloads. It currently runs the host's ordinary build prerequisites, including Watchdog publishing. A managed-reference-only build that avoids unrelated packaging, signing and NativeAOT helper publishing remains pending; the SDK must not silently change target frameworks or omit required functionality.

Reference import, managed compilation, native builds, application bundling/signing and target-platform execution are separate capabilities. The SDK is intended to support cross-compilation where the toolchain permits it; the complete Everywhere matrix has not been validated. Having the source checkout does not automatically make every platform project buildable on every OS.

For multi-platform mods, provide a host input for each requested target. Missing macOS inputs should not block an explicitly Windows-only build. Packaging a mod that declares both Windows and macOS entries requires both artifacts; it must not silently omit one. See section 5 for project organization.

The intended author/agent workflow is to create a project, configure local host inputs, write source and project declarations, build the requested entries, validate/package generated metadata and test against the selected host. Authors and coding agents never hand-write or repair generated JSON. Templates, CLI commands and analyzer configuration still need implementation.

XML documentation will be generated/retained with host references where available. `PreserveCompilationContext` is not a prerequisite for installation imports and does not need to ship a full `refs` directory. The maintained SDK tests cover ordinary directory and source imports; published cross-platform builds remain a release gate.

The runtime project is `src/Everywhere.Modding.Hosting/Everywhere.Modding.Hosting.csproj`. Mod authors implement `Everywhere.Modding.Abstractions.IMod`. Retained historical lifecycle probes still require migration to the replacement API before their results can validate this rewrite.

### Access host internals when needed

For trusted mods that need internal host APIs, [IgnoresAccessChecksToGenerator](https://github.com/aelij/IgnoresAccessChecksToGenerator) is the recommended opt-in build tool. The repository already uses it for patch projects. Declare the specific assembly names you need:

```xml
<ItemGroup>
  <PackageReference Include="IgnoresAccessChecksToGenerator" Version="1.0.0" PrivateAssets="all" />
  <IgnoresAccessChecksTo Include="Everywhere.Core" />
</ItemGroup>
```

It generates compilation references exposing internal members and adds the runtime access-check attribute to the caller. The host DLL is not modified. Generated compilation references must never be distributed as replacement runtime DLLs. Internal APIs remain host implementation APIs, so declare and test the appropriate host version range. The upstream tool requires CompileUsingReferenceAssemblies=false when operating on ordinary ProjectReference inputs, including source-host imports. Installation imports use runtime DLL references. Both import workflows still need generator integration tests; the SDK does not enable this option globally.

Access checks and inheritance restrictions are different. Ignoring access checks can permit use of an internal non-sealed base class; it does not permit deriving from a sealed class or overriding a non-virtual method. Use composition or an existing interface when it fits. If inheritance is an intended host extension point, that host class needs a deliberate source-level change and appropriate overridable members. A runtime method hook changes execution; it does not turn a class into a legal base class.

## 3. Lifecycle API

Register the disposable handles that reverse changes your mod makes to the application. During hot
unload, the engine disposes those handles to remove the changes. Independently, an entry that owns
resources may implement IDisposable or IAsyncDisposable to perform its normal final cleanup, including
when Everywhere exits. Entries without final cleanup need neither disposal interface.

**Implemented:** IMod requires only InitializeAsync. The engine discovers optional disposal
interfaces on the entry instance, prefers IAsyncDisposable when both exist and never calls both or
falls back to Dispose after an asynchronous disposal failure. It also disposes a constructed instance
after initialization completes with an error. No disposal interface is needed for an entry that only
registers reversible contributions and owns no resources needing final cleanup.

Use a public, concrete, non-generic entry class with a public parameterless constructor. Keep constructors
free of subscriptions, background work and patches: a failed constructor cannot return an instance for
final disposal. Initialization may fail partway through, so cleanup must tolerate partially acquired state.

InitializeAsync receives an IModContext with these existing members:

| Member | Responsibility |
|---|---|
| ModId | Logical identity of the activation |
| Services | Activation-local services with shared host singletons; stop using them before entry disposal completes |
| Stopping | Signal to stop accepting work and finish outstanding operations |
| StopReason | Null while running; Unload or Exit is set before Stopping is signaled |
| Register | Register a disposable handle that reverses an application change made by this activation |
| Mods | Activation-relative availability and host-owned mod information |
| InstallationDirectory | Installed package files, read-only by contract |
| DataDirectory | Persistent data owned by this mod ID |

Use `context.Services` for service resolution, including generic loggers. Do not use the internal
application `ServiceLocator`, obtain a root provider through access-check bypasses, or retain the
activation provider after stopping. Child scopes belong to the activation and must be disposed by
their callers before mod cleanup completes. Settings control factories capture this provider or
their required dependencies; presenters do not supply a container.

Generic loggers, lazy relationships and keyed services use this activation boundary. Let the
container dispose services it creates; do not register those instances again with `context.Register`.
See [Service Boundary](07-ServiceBoundary.md) for service ownership and retirement rules.

The lifecycle follows ordinary object ownership. Keep short-lived files inside using or await
using scopes. Retain ongoing work so you can stop and await it before closing the resources it uses.
Flush buffered output and release connections in the entry's normal disposal, not only in registered
change-reversal handles. Prefer asynchronous disposal when the entry implements both interfaces; it
is one finalization responsibility, not two callbacks. Finalization also applies when an instance is
retired by hot unload or after completed initialization failure, not only when the process exits.

An entry declared as IMod, IAsyncDisposable can release its owned StreamWriter as follows.
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

The writer is ordinary owned IO: it is not registered with context.Register. A mod with background
writers must stop and await them before this step. Keep important data saved during normal operation;
final disposal cannot guarantee persistence during forced termination or OS shutdown. Do not fetch UI
state at the last moment or depend on a running dispatcher during ordinary process exit.

Hot unload closes activation registration, signals Stopping, awaits entry disposal, and then reverses
registered changes. Assembly registrations are reversed when their owners retire, before unloading and
collection checks. Keep the state needed by removal handles usable after entry disposal. A patch or
subscription may still be installed while the entry is disposing: reject new work and await existing
callbacks before releasing what they use. The engine does not track those callbacks for you.

Use context.StopReason to distinguish these paths: Unload covers live removal, replacement and
cleanup after completed initialization failure; Exit marks ordinary process shutdown. The reason is
available inside Stopping callbacks and entry disposal. If exit joins an unload already in progress,
its reason remains Unload; the existing disposal operation is not restarted with different semantics.

Ordinary application exit uses DI disposal after the UI lifetime ends. It signals Stopping and awaits
entry disposal, but skips both reversal containers, assembly unloading and collection checks. Do not
resolve new host services or await the UI dispatcher in this path. Fast-close means skipping hot-unload
work, not a five-second deadline; a disposer that does not finish can delay exit. Mod lifecycle and
reversal callbacks must not recursively apply mod changes or dispose the engine.

For navigation contributions, create your IMainViewNavigationTopLevelItem on the UI thread and call
MainViewModel.RegisterNavigationItem. Register the returned removal handle with context.Register; its
asynchronous disposal dispatches removal to the UI thread. Register the LocaleManager.RegisterProvider
handle before creating localized controls. Use the existing I18N generator and dynamic keys. The
[sample](../../mods/samples/Everywhere.Mods.Sample/README.md) demonstrates these registrations with
compiled AXAML and RESX. Its entry needs no disposal interface because its contributions are registered reversals.
For local validation, put the generated ZIP directly in `~/.everywhere/mods`, or put an unpacked package folder there with `mod.json` directly inside it. Keep one source per mod. Start the newly built mod-enabled application. Startup imports immutable runtime copies into the application data directory and reuses unchanged imports. Source additions, replacements and removal take effect on the next launch; no management page or live watcher is provided. On Windows, installation copies live in `%APPDATA%/Everywhere/cache/mods`, temporary imports in `%APPDATA%/Everywhere/cache/mods/staging`, and persistent mod data remains under `~/.everywhere/mods-data/{id}`. Do not edit generated runtime copies. After exiting Everywhere, you may delete the entire `cache/mods` directory; the next startup rebuilds it from the retained sources without deleting mod data. The initial UI sample passed headless behavior and cleanup checks but returned `RestartRequired` on collection verification, so it does not establish support for hot-unloading Avalonia controls.

## 4. Owning resources and side effects

### Register application changes, own ordinary resources

Every change to shared application state that must disappear when your mod is removed needs an
owned reversal registered with the engine. A host API may return a disposable registration handle or
explicitly register it for you; do not register the same reversal twice. Examples include event
subscriptions, navigation contributions, patches and class-level framework registrations. Registration
does not inspect the object or infer what to undo: that behavior must be implemented by its disposer.

Keep context.Register and ModResource.Register as the names. They accept resource objects rather
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
| Mod entry itself | Do not register it; entry finalization is a separate responsibility |

For example, these statements register the removal handle, not the page as an arbitrary object:

```csharp
var registration = navigation.RegisterNavigationItem(page);
context.Register(registration);
```

This fragment runs on the UI thread, with navigation and page already supplied by the mod. The
returned handle's disposal reverses the navigation change. A file writer used by the same mod stays
under normal object ownership. Do not register its final save as if saving were reversal of a host change.
Persistent mod data is meant to survive unload; external writes and requests are not automatically
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
mod actions or choose a different entry-disposal order for each mod.

This is an ownership sketch, not a thread-safe asynchronous disposal template. When asynchronous release
can overlap, callers must await the same completion task. A boolean meaning "release started" is not
proof that release finished. Do not assume every undo action is repeatable: restoring an earlier setting
twice could overwrite another mod's later change. Coordinate shared resource release without repeating
the whole reversal. A caller-created ModResource can own ordinary resources, but each handle must
still have one owner; putting the same resource into two containers does not coordinate their disposal.

### Choose the owner of the registration

Use context.Register(resource) for changes belonging to this activation. The engine reverses them
when that instance is retired, even if a shared library remains loaded for another mod.

Use static registration for changes belonging to a loaded assembly, including those made before
InitializeAsync receives a context:

```csharp
static MyControl()
{
    ModResource.Register<MyControl>(CreateClassSubscription());
}
```

CreateClassSubscription represents an acquisition method returning a disposable subscription whose
disposer removes that subscription. Equivalent forms are ModResource.Register(typeof(MyControl),
resource) and ModResource.Register(typeof(MyControl).Assembly, resource). Use a type defined in the
intended mod library, not List<MyControl>: the defining assembly determines ownership, not generic
arguments or the runtime type of the returned handle. No fallback container is created for host or
independently loaded assemblies.

If A and B share a library, its class-level registrations must survive removal of A while B still uses
it. They belong to that library's loading instance, not whichever mod first ran its static constructor.
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
mod-owned files or connections have no cleanup: they use the optional entry-disposal contract in
section 3. Installed callbacks may still be reached before termination and
must tolerate their owner having stopped, without starting new work or touching already released state.
Static registration provides no extra process-exit callback for a shared file writer or other static IO
object; such an object needs an explicit ordinary owner.

A manually created new ModResource() is a resource container owned by its caller. Its DisposeAsync
really disposes the accepted objects; the engine's process-exit policy does not disable that operation.
Call Close if registration must stop before users drain, then await the first DisposeAsync operation.
Repeated calls do not repeat cleanup or wait for an earlier in-flight call. This general container
behavior does not turn static registration into application-exit resource management.

### Avalonia controls and framework registrations

**Framework integration remains under investigation.** ModResource now provides assembly-owned
cleanup registration. The earlier full-type-scan/UnregisterByModule proposal is not the general solution:
it misses framework registrations and can force resolution of absent optional types. Automatic tracking
inside Avalonia is a candidate for unchanged third-party controls, not an implemented capability.
Authors still own ordinary subscriptions and UI contributions; the current sample requires restart
when collection is unconfirmed. Process exit skips registered-resource cleanup.

The navigation sample exposed property metadata caches holding its `SamplePage` type even though it defines no new Avalonia properties. An isolated probe removed those roots with the public API and confirmed both assembly and load-context collection. The current application path still reports `RestartRequired` until that cleanup is integrated. This result covers this sample, not all custom controls.

See [Known issues](03-Validation.md#known-issues) for the open limits affecting custom properties, AddOwner, class handlers, layout/render helpers, routed events and host-owned UI state. That chapter distinguishes reproduced failures, source-confirmed reference paths and untested components, and records current handling and the work needed to resolve each issue. These are known limits, not a blanket prohibition on custom controls.

For a class handler that exposes a token, register it during activation. For example, on the UI thread, with `ModView` being your control type:

```csharp
context.Register(
    Control.IsVisibleProperty.Changed.AddClassHandler<ModView>(
        static (view, _) => view.InvalidateVisual()));
```

This makes this particular subscription reversible; it does not remove unrelated registrations made by the control. A mod-owned static field is not inherently an unload leak: the problematic path is a surviving host/framework object retaining a mod type, delegate or instance. Do not clear shared framework tables through reflection or unregister host types as a generic workaround. Use the engine's collection result to determine whether the current process can safely continue changing mods. Source-reviewed limits and experimentally confirmed behavior are separated in [Validation](03-Validation.md#avalonia-cleanup-evidence-and-limits).

### Persist mod settings

Use context.OpenSettingsAsync to obtain SettingsEngine<TSettings> when the mod needs application
JSON patching, observation and migration behavior with its own document. Follow the [settings developer guide](06-SettingsDeveloperGuide.md)
to declare metadata, open settings during initialization and save them during entry disposal.
Presentation is independent; the optional mod-wide registry is a separate integration.

## 5. Dependencies and conditional code

Multiple mods can share types because the loader selects one assembly instance per managed simple name. A conflicting dependency version is rejected rather than hidden in a private copy with incompatible type identity. Host assemblies and the lifecycle contract must not be shipped as competing mod-owned DLLs.

Active dependencies must form directed acyclic graphs. A -> B -> D and A -> C -> D is valid; A -> B -> A is not. A selected dependency loads and initializes first and stops last, including an optional dependency selected for this activation. Updating a shared library may require reloading all consumers.

Package version, AssemblyVersion and file hash describe different things. Tooling will record the exact build selection, the author-declared allowed runtime range and the combinations actually tested separately. Matching a range is not proof that an untested future version works. Defaults for other-mod and library dependency ranges still need implementation decisions; the host-range policy is defined in section 6.

The runtime accepts selected files and explicit required/optional dependency declarations. It implements the catalog, conditional entry validation, controlled name-based binding and whole-consumer reconstruction below. Missing undeclared references are rejected. It does not select package versions, generate all optional dependency declarations from author projects or provide complete conditional type-discovery/analyzer tooling.

### Declare optional dependencies in the project — agreed, not implemented

Declare that your mod can run without another mod, rather than marking your whole mod LazyLoad. The engine may still prepare available dependencies before your mod to preserve load order and lifetime behavior. No promise is made to defer a DLL until its first call.

The agreed SDK authoring form is:

```xml
<PackageReference Include="Acme.Translator.Mod"
                  Version="1.4.2"
                  ModDependency="Optional"
                  ModVersionRange="[1.4,2.0)" />
```

`ModDependency` and `ModVersionRange` are custom fields for the future SDK, not existing NuGet features. Version selects the package used to compile; ModVersionRange declares which logical mod versions you permit at runtime. Ordinary mod references default to required. The SDK obtains the mod ID and assembly mapping from the referenced mod's build metadata. ProjectReference will support equivalent intent. Dynamic-only dependencies without a build reference will use an explicit ModDependency project item; its detailed syntax is still reserved.

Do not write generated manifest JSON. Change the source/project declaration and rebuild. Defaults for other-mod dependency ranges remain undecided, so these examples specify a range explicitly.

### Query whether another mod is usable — implemented

`IModContext` exposes `IModCatalog Mods { get; }` with these operations:

```csharp
public interface IModCatalog
{
    bool IsAvailable(string modId);

    bool TryGetInfo(string modId, [NotNullWhen(true)] out ModInfo? info);
}
```

The signature uses NotNullWhen from System.Diagnostics.CodeAnalysis. The catalog returns information, not another mod's lifecycle instance.

| Question | API / meaning |
|---|---|
| Can my mod use B in this activation? | `Mods.IsAvailable("B")`: B is a declared, compatible, selected dependency that finished initialization |
| Does the host know B? | `Mods.TryGetInfo("B", out var info)`: no loading or enablement side effect |
| Did the user request B to be enabled? | `info.IsEnabled`: desired configuration, not successful initialization |
| What is B doing? | `info.State`: `Inactive`, `Initializing`, `Active`, `Stopping`, `Failed`, `Blocked` or `Skipped` |

Use IsAvailable to guard calls. A mod can be enabled but skipped for an unsupported platform or failed, or globally active but incompatible with your dependency range. Querying IsAvailable for an undeclared dependency is an error; use TryGetInfo for general information about arbitrary mods. A declared optional dependency that is absent, disabled, platform-skipped or incompatible returns false. `TryGetInfo` exposes `SkipReason = UnsupportedPlatform`; skipping does not satisfy a required dependency. Querying an unknown ID with TryGetInfo returns false.

ModInfo is an immutable snapshot containing identity, version and status information. It does not contain Assembly, Type, mod instances, delegates or exception objects that could keep another mod alive. A snapshot can become out of date and does not retain a dependency for you. There is no IsLoaded readiness flag: one mod may have several assemblies, and a stopped mod can still have uncollected assemblies. Physical residency belongs in engine diagnostics.

### Guard calls across a method boundary — runtime supported, analyzers pending

The following example assumes Acme.Translator provides TranslatorApi and is declared optional in the project. Everywhere.Modding.Abstractions and RequiresMod exist; SDK project declaration generation remains pending:

```csharp
using System.Runtime.CompilerServices;
using Everywhere.Modding.Abstractions;

public static class TranslationFeature
{
    public static string GetText(IModContext context)
    {
        if (!context.Mods.IsAvailable("acme.translator")) return "Hello";
        return Translate();
    }

    [RequiresMod("acme.translator")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Translate()
    {
        return Acme.Translator.TranslatorApi.Translate("Hello");
    }
}
```

Each part has a different job: IsAvailable guards execution, NoInlining preserves the call boundary, and RequiresMod tells the SDK and loader which dependency the method requires. RequiresMod does not inject a check, change CLR behavior, or add NoInlining automatically. NoInlining does not prevent explicit reflection or pre-JIT from reaching the method.

All required mod IDs must be declared in the project. Multiple RequiresMod attributes require every condition to be satisfied; they do not express alternatives. Source annotations do not override compatibility ranges.

### Inherit from an optional mod's type — runtime supported, discovery tooling pending

An optional derived type can stay in your ordinary entry DLL. A separate integration DLL is useful if you need a separate removal boundary, but is not mandatory. For example, assuming the dependency supplies PanelBase with a parameterless constructor and a Render method:

```csharp
[RequiresMod("acme.translator")]
public sealed class TranslationPanel : Acme.Translator.PanelBase
{
}

public static class OptionalPanelFeature
{
    public static void Render(IModContext context)
    {
        if (!context.Mods.IsAvailable("acme.translator")) return;
        RenderPanel();
    }

    [RequiresMod("acme.translator")]
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

Your available dependency set is chosen before InitializeAsync; selected dependencies initialize first. During normal execution, the host keeps that selection stable. If B is enabled, disabled or replaced, the engine can stop and reconstruct your entire mod A. It also includes affected supporting libraries and consumers, while preserving unrelated mods where possible. It does not change existing base types or detach bindings inside your surviving context.

After reconstruction, InitializeAsync receives a new context and checks the new availability. No dependency-change event is required for this first API. A previously absent optional dependency is still recorded in declarations so the engine knows that enabling it affects A.

When Stopping is signalled, reject new work and await outstanding calls before shutdown completes. The engine drains consumers before stopping their dependencies. IsAvailable is not a task tracker or a lifetime lease; retaining callbacks or failing to await work can still prevent unloading and require restart.

Optional does not mean ignoring initialization errors. If B was selected but fails initialization, the activation attempt fails and is cleaned up. The engine does not continue initializing A with an altered dependency set. A later explicit attempt without B is allowed only after cleanup and collection have been confirmed.

### Use declared name-based loading — agreed, not implemented as a full policy

The intended API is ordinary `Assembly.Load("Acme.Translator")`; no ModContext.LoadAssembly wrapper is planned. Declare the dependency even if reflection is your only access path. The argument is an assembly name, not a mod ID. Check mod availability before using mod APIs: obtaining an Assembly is not proof of successful initialization.

Calls entering the controlled resolver must return the selected instance and reject unavailable, undeclared or incompatible bindings. Calls made by collectible supporting libraries need declarations as well. Default-owned shared libraries may need the host to establish a scoped contextual-reflection environment; do not assume every Assembly.Load call in the process uses your mod's resolver. Representative third-party libraries and async/concurrent propagation still need validation.

LoadFile, byte-array loading and custom loading contexts bypass this supported ownership path. If your code or a dependency uses them, the engine cannot promise coordinated unloading of those assemblies. Runtime-generated assemblies and expression compilation also need lifetime checks; neither automatic rejection nor guaranteed collection follows solely from their being dynamic.

The rewritten resolver enforces requester declarations and reconstructs consumers when optional availability changes. The current fixture tests exercise these transitions and controlled name-based binding. SDK dependency declaration generation and authoring analyzers remain pending. See [Validation](03-Validation.md#historical-findings-and-their-limits) for the limits of the historical experiments.

### Organize platform-specific projects — design agreed, tooling pending

A mod has one ID and version and one enable/update/lifecycle identity. It declares one universal entry or several mutually exclusive platform entries. Each entry names its assembly and concrete IMod class; only one runs in a given activation. Supporting libraries need no entry class. Use separate mod IDs if two features need independent lifecycles or updates.

An ordinary neutral mod uses one project and a simple folder/archive. For platform APIs, a useful organization is:

```text
Acme.Clipboard/
  Mod.props                  Shared mod ID and version declarations
  Everywhere.local.props       Ignored machine-specific host locations
  Everywhere.local.props.example
  Common/                       Supporting code, no lifecycle entry required
  Windows/                      Windows entry project -> Common
  Mac/                          macOS entry project -> Common
```

These are illustrative project paths, not required package paths. Both platform projects build the same mod identity/version. Common must not reference the platform entry projects. Shared source compiled once per target is another valid approach. Each platform entry compilation must use a target framework compatible with the host projects it references: for example, a net10.0 project cannot directly reference a net10.0-windows10.0.19041.0 host project merely by setting RuntimeIdentifier to win-x64. Neutral code should reference neutral host projects. Use normal target framework, RID and minimum-OS settings where possible; mod-specific settings cover additional intent. Do not choose cross-target source files solely from the build machine's OS.

The Windows and macOS host projects both emit `Everywhere.dll`. Compile each mod entry against its matching host set; do not add both host files to the same compilation. A neutral target framework alone does not prove that all APIs you use work on every OS.

The loader selects the entry and applicable common/platform assets before dependency validation. Unselected platform files do not create active dependencies. `OperatingSystem.IsWindows()` guards execution but does not remove an AssemblyRef. Keep unavailable platform dependencies in separately selected artifacts or conditional builds. RequiresMod concerns another mod's availability; it does not replace platform selection.

The SDK packages satellites from the standard MSBuild resource outputs and preserves their target paths. Host-provided resources come from the host's .deps.json resources section and are excluded without adding them as compilation references. At runtime, resource-only satellites are associated with their parent DLL by assembly metadata and loaded into that parent's collectible context. There is no hard-coded language list or language-folder discovery. ResourceManager provides normal parent-culture and neutral fallback. Multi-entry aggregation and cross-platform native validation remain incomplete.

### Native libraries and executable content

Use the normal build/RID output. The SDK copies its declared output paths and generates separate
`assemblies`, `nativeLibraries` and `contentFiles` lists. You do not maintain these lists manually.
Set the entry platform to the actual supported architecture, such as `win-x64`, when shipping an
architecture-specific DLL. NuGet restore selects its runtime assets; the mod loader does not
download packages or recreate NuGet restore at activation time. Keep the generated `.deps.json`.

Normal references, NuGet runtime/native/resource assets and generated satellites are classified by
MSBuild. A damaged managed output fails packaging instead of being reclassified as native.
Content/None items copied to the output stay content, even when named `.dll`.
For a manually supplied binary in the mod project, declare its role on the copy item:

```xml
<ItemGroup>
  <Content Include="vendor/search.dll" CopyToOutputDirectory="PreserveNewest" AssetType="native" />
</ItemGroup>
```

Use `runtime` for a manually supplied managed assembly and `resources` for its satellite;
prefer ordinary Reference/ProjectReference/PackageReference items whenever available.
These explicit Content/None roles apply to items declared in the mod project; normal referenced
projects and NuGet dependencies use their resolved asset lists. `Link` or `TargetPath` controls the
package-relative path. Helper executables remain content, including `.exe` files delivered as NuGet
native assets. The SDK does not inspect the native dependency tree or preload these files.

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
user-owned from mod-owned processes. Mutable configuration/databases belong in DataDirectory;
copy an executable there first if it writes beside itself. Native lifetime validation remains
specific to each library and platform.

### Register chat tools without retaining their implementation

Use ModChatPlugin and NativeChatFunction with IChatPluginManager.RegisterChatPlugin.
See [Chat Tool Integration](04-ChatToolIntegration.md#author-workflow) for registration lifetime,
approval hooks, JSON metadata and the limits of retained snapshots.

### Mod settings and settings pages

context.OpenSettingsAsync opens a mod-owned SettingsEngine<TSettings> from
`IModContext.DataDirectory/settings.json` using the shared settings pipeline, explicit JSON metadata
and mod-local migrations. IModSettingsRegistry publishes lazy SettingsItems without changing
IMod. Both are implemented in the working tree; the management page is not part of this batch.

The [Settings Developer Guide](06-SettingsDeveloperGuide.md) shows generated descriptors, opening,
UI registration, final saving and migration declarations. Keep the engine entry-owned so ordinary
exit saves as well as hot unload. Do not substitute the host's Settings singleton or root migration
container for this integration.

### Declare package names, descriptions and README files

Add one project item for each language you supply. `Include` identifies the language; `Name`,
`Description` and `Readme` are independently optional. An empty field is omitted. You may omit
names entirely, in which case the management UI will use the mod ID.

```xml
<ItemGroup>
  <EverywhereModLocalization Include="en">
    <Name>Everything Search</Name>
    <Description>Search local files and folders with Everything.</Description>
    <Readme>README.md</Readme>
  </EverywhereModLocalization>
  <EverywhereModLocalization Include="zh-Hans">
    <Name>Everything 搜索</Name>
    <Description>通过 Everything 搜索本地文件和文件夹。</Description>
    <Readme>README.zh-Hans.md</Readme>
  </EverywhereModLocalization>
</ItemGroup>
```

Keep these items in a shared `.props` file when multiple entry projects build the same mod.
They describe the whole package, not one platform entry. Language tags ignore case; duplicate
language items are rejected. These declarations do not require an I18N or UI assembly reference.
Your runtime RESX resources and satellite assemblies remain a separate, ordinary .NET mechanism.

README mappings can be omitted for conventional root-level files: the SDK packages `README.md`
as English and `README.<language>.md` for that language. Explicit `Readme` metadata overrides the
convention for its language and prevents the same source file from being inferred as another
language, even when Link changes its package-relative path. For example, `<EverywhereModLocalization Include="zh-Hans" Readme="README.md" />`
supports a Chinese-only mod. Existing names such as `README-zh-cn.md` work when explicitly mapped.
If both `README.md` and `README.en.md` exist without an explicit English selection, packaging reports
an ambiguous English README. No recursive README search is performed.

`Readme` is a package-relative path. A mapping such as `Readme="docs/guide.md"` includes the project
file at that path unless the normal build output already supplies it. To share a README between
entry projects, use an ordinary content item such as
`<Content Include="../shared/guide.md" Link="docs/guide.md" CopyToOutputDirectory="PreserveNewest" />`
and declare `Readme="docs/guide.md"`. No additional SDK source-path mapping is needed.
Add images and other attachments through normal content items, preserving the paths used by
relative Markdown links:

```xml
<ItemGroup>
  <Content Include="docs/images/**/*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Each field falls back independently: current language, parent tags, English, then the first supplied
value. A missing Chinese name can therefore use the English name alongside a Chinese description
and README. Prefer `zh-Hans` for the application's Simplified Chinese locale and `zh-Hant` for
Traditional Chinese. `zh-CN` and `zh-Hans` are different tags; case differences such as `ZH-hans`
and `zh-Hans` are equivalent. The SDK generates lowercase keys in `mod.json`; do not edit it.
The host can read these declarations while the mod is unloaded. The management page remains pending.

The `README.<language>.md` convention follows [Standard Readme](https://github.com/RichardLitt/standard-readme/blob/main/spec.md).
Language-tag case insensitivity follows [BCP 47](https://www.rfc-editor.org/rfc/rfc5646.html#section-2.1.1).

### Distribution, installed files and mod data

Before distributing a mod:

1. Build against the host you intend to support. If the entry references host implementation APIs, set `EverywhereHostVersion` to an explicit tested range; packaging requires it and verifies that it includes the imported host.
2. Review Mod API compatibility. Keep the SDK default described below, or set `EverywhereModApiVersion` to a tested range that includes the imported contract. Do not edit the generated manifest.
3. Run `dotnet msbuild <mod.csproj> -restore -target:EverywherePack` with the configured SDK and host input. This produces the distribution ZIP.
4. Test that ZIP with the supported host versions before publishing it. A declared range is your compatibility claim, not evidence that every version in it works.

> [!IMPORTANT]
> **The SDK automatically supplies Mod API compatibility when `EverywhereModApiVersion` is omitted.**
> A stable Mod API `1.2.0` produces `[1.2.0,2.0.0-0)`: it accepts later compatible 1.x versions,
> including prereleases above the lower bound, but excludes **all 2.0.0 prereleases and releases**.
> A prerelease Mod API `1.2.0-preview.2` produces the exact range `[1.2.0-preview.2]`.
> These defaults concern the Mod API, not the SDK package version or the Everywhere host version.
> An explicit `[1.2.0,2.0.0)` has a different meaning: it includes `2.0.0-preview.1` because that version
> precedes `2.0.0`. There is no additional prerelease filter. See [the version contract](../Versioning.md).

The SDK produces a standard ZIP. The host extracts it once into a staging directory, validates it, and moves the complete directory to a unique final installation path before activation. Package-relative paths are preserved. It loads from that directory, not from the ZIP or a newly extracted temporary copy at each launch. Startup accepts either a ZIP or an equivalent unpacked package folder under ~/.everywhere/mods. The original source is the user-facing installation unit; keep it there. Unchanged sources reuse their imported copies.

Each installation gets its own directory, including rebuilds with the same mod version. Treat package files as read-only while installed. Read bundled resources from `IModContext.InstallationDirectory` and write configuration/databases to `IModContext.DataDirectory`, the stable directory associated with your mod ID. Updating or uninstalling code must not implicitly delete persistent data. Replace or remove original packages while the application is stopped. Source removal excludes the mod on the next launch and preserves its data.

Check the startup log after installing or replacing a package: a successful import does not guarantee activation, and dependency conflicts can leave it inactive. The application currently imports sources only at startup; live update controls, persisted disablement, restart handoff and automatic cleanup of superseded installations are not available. The runtime update API retires affected owners before validating and activating replacements. If retirement requires restart, replacement loading does not begin. Failed replacements do not automatically restore the old activation. See the [operation results](#8-understanding-operation-results) for failure handling.

Keeping an older installation makes explicit code rollback possible, but it does not undo database migrations or external effects. Design data compatibility and recovery accordingly. See the [installation contract](01-Specification.md#package-installation-updates-and-persistent-data) for staging, persistence and cleanup rules.

## 6. Generated entries and compatibility

**Design agreed; complete schema/tooling pending.** You declare intent in projects and source; the SDK generates metadata from the resolved build inputs and final runtime files. The host rechecks declarations and actual files. Build locks and exact build versions support reproducibility; they do not automatically declare compatibility with other versions.

The agreed entry shape for one mod with two platform implementations is shown below. This is an explanation of generated output, not a manifest to maintain by hand or a complete package schema:

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
        "type": "Acme.Clipboard.Windows.Mod",
        "hostVersion": "[0.9.0,0.10.0)"
      },
      {
        "platform": "osx",
        "assembly": "macos/Acme.Clipboard.Mac.dll",
        "type": "Acme.Clipboard.Mac.Mod",
        "hostVersion": "[0.9.2,0.10.0)"
      }
    ]
  }
}
```

A universal entry uses `"$type": "universal"`, with `assembly`, `type` and any applicable `hostVersion` directly on `entry`. A single-platform mod uses `perPlatform` with one variant. `$type` identifies the metadata shape; `type` is the full entry class name. Paths are package-relative; the example directory names are not a frozen layout.

Platform names use portable .NET RIDs: `win`, `win-x64`, `osx`, `osx-arm64`. Use `osx`, not `macos`, for the platform value. `win` does not mean x64: it allows compatible Windows process architectures subject to the actual assets' constraints. If both `win-x64` and `win` entries exist, a Windows x64 process selects the specific one. Matching follows the RID graph, not a prefix or array position. Duplicate platform declarations and unresolved ambiguity are errors.

When no platform entry matches, the mod stays enabled but has `State = Skipped` and `SkipReason = UnsupportedPlatform`; it executes no entry code. This is a per-mod status, not an activation failure. Malformed metadata and incompatible selected entries still fail validation. The loader selects the platform entry first, then validates its compatibility. It does not fall back from an incompatible `win-x64` entry to `win` just because the latter permits the installed Everywhere version. Only the selected entry's dependencies and common applicable assets participate. Record the minimum OS/runtime separately from RID where needed; generic platform spelling cannot make architecture-specific native files portable.

### Diagnose startup source imports

Keep one ZIP or unpacked package folder per mod ID under `~/.everywhere/mods`. If a source
cannot be read or validated, startup logs its path and diagnostic code and still imports the other
valid sources. If several sources declare the same ID, none of those sources is selected; keep the
one you intend to use and restart. Other mod IDs are unaffected by this duplicate-source check.

Fix or replace the broken source and restart to retry. Unchanged successful imports reuse their
runtime copies, and mod data is preserved. A package that imported successfully may still fail
later compatibility, dependency or activation checks; source import success does not mean it started.
Source discovery remains startup-only, without a live watcher or automatic runtime removal on failure.

### Declare compatibility for the entry you build

Your mod has one exact version across its entries. The SDK version identifies build tooling; the Mod API requirement describes the host-owned abstraction contract. Everywhere's version is a separate constraint when your entry uses implementation APIs. Multiple host releases can share one Mod API version.

Host compatibility belongs to the concrete entry, so Windows and macOS entries may declare different `hostVersion` ranges. For entries that reference host implementation APIs, local development can omit this range while tooling records the imported build. Distribution packaging requires an explicit permitted range; tooling may help you specify the exact imported version as a starting point. A broad range does not prove future versions work. Contract-only mods need Mod API compatibility without an unnecessary host-release restriction.

The public Mod API policy preserves backward compatibility within one major version. The SDK implements the stable and prerelease defaults explained in the [distribution steps](#distribution-installed-files-and-mod-data). This API policy does not infer compatibility for internal host APIs or arbitrary NuGet packages.

Host, Mod API and mod dependency ranges share [Everywhere's version rules](../Versioning.md). [1.2.0] means exact compatibility; bare 1.2.0 means an inclusive minimum; [1.2.0,2.0.0) includes the lower bound and excludes the upper bound. Parentheses exclude a bound, and either bound may be omitted. Floating versions such as 1.* are not supported in mod metadata.

Prerelease labels compare segment by segment, independently of update channels: `preview.10` follows `preview.2`, and a stable version follows prereleases with the same numeric components. Metadata such as `+commit` does not distinguish versions for compatibility checks, including exact ranges.

Mods that need to inspect versions can use Everywhere.Common.SemanticVersion and SemanticVersionRange through an Everywhere.Abstractions reference, or explicitly reference the host-provided Everywhere.Versioning assembly. The SDK's tool dependencies are not automatically mod compilation references or package payload. Ordinary PackageReference dependencies still follow NuGet restore rules.

Defaults for other-mod dependency ranges remain undefined. Specify an explicit range when declaring such dependencies; do not interpret an omitted range as unlimited compatibility.

SDK validation will cross-check dependency intent, RequiresMod annotations and final artifacts. Referenced metadata maps mod IDs to package origins and assembly identities without making authors repeat them. Repair validation errors in source/project inputs and rebuild. Do not edit generated JSON to hide conflicts, widen ranges or add absolute host paths.

## 7. Patching and special cases

Patching is optional. Add MonoMod.RuntimeDetour to your mod project only when your feature needs it; neither the Everywhere SDK nor the runtime includes it as a default dependency or exposes a special patch API. RuntimeDetour Hook is the starting point supported by the recorded non-generic experiments. Experiment results establish selected capabilities, not universal method support. Other patch libraries remain the mod author's responsibility.

Register each owned hook with `context.Register(hook)` immediately after creation: its disposal reverses the patch. Stop accepting new mod work and await in-flight callbacks before retiring their state. Entry-owned resource disposal is separate from handle reversal; see section 3 for optional entry disposal and callback-draining responsibilities. Process exit leaves registered hooks installed until termination. A stopped hook must therefore safely pass through or otherwise handle later host calls without starting new mod work or accessing state already released by its owner. Do not dispose another mod's handles, retain continuation delegates, or assume the engine automatically tracks patch callbacks. Hook registration uses the registered change-reversal contract; it is not a general process-exit resource queue.

The SDK handles the patch library and its dependencies through ordinary dependency rules. A shared library version must satisfy the selected mod set. Earlier experiments loaded RuntimeDetour in Default; mod-managed ownership is a separate deployment condition that still needs validation against the rewritten runtime. Do not interpret those experiments as a promise that every privately packaged patch library can unload.

| Situation | Guidance and boundary |
|---|---|
| Two mods hook the same ordinary method | Use the library's explicit ordering and keep handles separately owned. Tested Hook mods can be removed independently under the recorded deployment; verify the actual shared-library selection and collection as well. |
| Patch callback is running during shutdown | Stop accepting mod work and await active callbacks before disposing resources. The loader cannot discover every task automatically. |
| Initialization fails after installing a hook | Registered cleanup plus partial-initialization-safe shutdown can remove it and permit a later retry. Unregistered mutations have no automatic recovery. |
| Callback passes straight through after shutdown | It may still be installed. Verify actual disposal separately from restored return values. |
| Shared generic type contains a mod type | Let the loader prepare declared dependencies first. Tested cases unload independently; do not retain objects/types/delegates in host caches. Arbitrary combinations remain conditional. |
| Target method is generic, such as GetTypeName<T>() | The tested Hook entry rejects this case. Host extension points, caller/startup rewriting and profiler techniques need separate designs and tests; no supported recipe is published yet. |
| Method was inlined into callers | Patching a method does not establish that those callers are intercepted. Validate the real deployment and call paths. |
| Satellite resources | Supported for resource-only satellites declared by the SDK. Identity/culture come from metadata; resources share the parent DLL's context. |
| Native imports | Native resolution remains incomplete. Native lifetime rules must not be inferred from managed collection. |
| Dynamic loading outside the selected plan | Breaks the loader's ability to enforce its selected universe. Declare runtime assets where possible; restart remains the fallback for uncertain lifetime. |

Never repair unload problems by blindly clearing undocumented CLR or patch-library caches. Do not retain continuation delegates beyond their supported invocation lifetime.

## 8. Understanding operation results

| Result | What to do |
|---|---|
| Applied | The selected set was applied. Inspect individual snapshots: an enabled platform-specific mod can be `Skipped`. |
| CompletedWithErrors | Inspect per-mod diagnostics. Failed or dependency-blocked mods did not become active; independent mods can still run. Correct the cause and explicitly retry. |
| RestartRequired | Do not assume the mod is disabled or its patch is removed. Further changes are blocked pending the application's save/restart workflow. |

A successful package import does not validate its entry type. The runtime checks the declared assembly path and type using inspected metadata, then verifies IMod when activating it. A damaged DLL, a missing entry type or an unsuitable type produces a diagnostic for that mod; independent mods can still start.

`Failed` identifies the mod's own preflight or lifecycle failure. `Blocked` identifies a dependency that prevents activation. `Skipped` remains reserved for unsupported platforms. A failed initializer is cleaned up without stopping successful dependencies or unrelated mods. Shared assemblies and their static registrations remain resident while a surviving mod needs them.

For example, if A and D depend on B, a failure in B blocks A and D; independent C still starts. If only A fails, B, C and D remain active. This also applies when B was declared optional but was selected as available: A cannot switch to its absent-B path halfway through the operation.

Hot update first retires affected old instances, then validates and starts the requested versions. Package metadata is inspected beforehand to determine which existing instances and bindings must change; it does not promise to keep an old version running when its replacement is invalid. Unchanged dependencies and shared DLLs stay available to surviving mods. Enabling an optional dependency can also require reconstructing an existing consumer.

If shutdown, registered cleanup or required assembly collection cannot be confirmed, loading does not begin and the result is `RestartRequired`. After successful retirement, a bad replacement stays failed; neither its predecessor nor an older dependency is restored automatically. Explicit removals remain effective even if a related update fails.

For example, updating A and D while they both use an unchanged B can leave A failed and D successfully updated, with B continuously active. A new consumer of that unchanged B can also start. If B itself is replaced, its old consumers stop first; failure of B's replacement blocks their restart. Sharing a DLL does not by itself make unrelated entries update together, but replacing that DLL or its established binding environment does require retiring its users.

A new mod conflicting with a still-running assembly owner fails without stopping the existing mods. The loading stage never widens the completed shutdown scope. Update the necessary existing users explicitly or restart if the new mod requires a different shared owner. The same boundary applies when adding a native provider would change a live resolver's source environment. Read actual active/resident snapshots alongside the desired configuration; an unsuccessful update may have already stopped the old version.

Retry is an explicit new operation, not an automatic callback loop. `RestartRequired` still freezes further changes when timeout, shutdown, cleanup or collection is uncertain. Successfully cleaning up a failed instance does not mean its shared DLL must disappear while another mod is using it.

A broken ZIP or folder in the startup mods directory does not stop other imports. Duplicate IDs exclude all their providers; keep one source per ID. Startup import does not implement live folder watching or hot-update selection.

## 9. Testing and agent workflow — implementation pending

The future CLI should support creating projects, building packages, checking a specified host/mod set and running lifecycle/behavior tests in isolated processes. Command names, test-host protocol and structured output schema are not yet available. A future Skill/MCP will reuse these operations.

Test successful initialization, partial initialization failure, shutdown failure/timeout, cleanup failure, repeat load/unload, retained host references and dependency replacement. For patches, check invocation counts/order, behavior before/during/after, and collection independently. Record exact host/runtime/dependency versions. Test the actual published app, not only a build output directory.

For optional code, test B absent, disabled, incompatible and available; exercise the actual optional method and derived type. Test enabling, disabling and replacing B after A has run, including same-AssemblyVersion replacements with different bytes. Confirm old callbacks and owners are released and unrelated mods remain usable. Include negative controls for unfiltered type discovery, unguarded optional entrypoints and undeclared dynamic loads. Successful prototypes do not replace these checks against the eventual production SDK and engine.

For SDK and packaging, test both host-import workflows, transitive host-library exclusion, incompatible shared-package versions, XML documentation and reference refresh after host changes. For platform entries, test exact and fallback RID matches, unsupported platforms, duplicate entries and an incompatible specific entry that must not fall back. Verify that foreign-platform dependencies stay outside the active graph. Test the intended installation and source-build combinations separately; a successful managed build does not replace execution on the supported target OS.

The remaining schema, commands and build properties are implementation work, not instructions authors are expected to guess. Startup installation and reuse are implemented; live update discovery, persisted enablement and interrupted installation handling remain pending. Native lifetime and advanced patching have separate design or validation work. The [specification's closure requirements](01-Specification.md#implementation-closure-and-final-merge) distinguish these from the completed SDK and installation architecture. Before final merge, finish all agreed in-scope work and reconcile the guide with working examples; explicitly agree and document any deferral. Implementation findings can require revising the design rather than preserving an earlier promise that cannot be supported.

Maintained tests, reproduction commands and known limits are indexed in [Validation](03-Validation.md).

The optional-dependency and conditional-discovery design was inspired in part by [tModLoader's cross-mod mechanisms](https://github.com/tModLoader/tModLoader/wiki/Expert-Cross-Mod-Content). The names and lifecycle promises above are Everywhere's contract, not a compatibility layer for tModLoader mods. tModLoader is [MIT-licensed](https://github.com/tModLoader/tModLoader/blob/stable/LICENSE); retain applicable copyright and permission notices when reusing substantial covered code. The [specification's attribution](01-Specification.md#design-references-and-attribution) explains the scope of this reference.
