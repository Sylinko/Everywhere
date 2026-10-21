# Mod validation

## Runtime cache layout check (2026-10-22)

The application now stores runtime copies and the import index in `cache/mods`, with staging in
`cache/mods/staging`. The package fixture passed all 16 cases. New ZIP and unpacked-folder cases
delete the entire inactive test cache, then verify reimport, source and persistent-data preservation,
an empty staging directory, a rebuilt index and reuse on the following import. Existing cases retain
coverage for source removal, source replacement, duplicate IDs and invalid packages.

Core and its test project built successfully. The explicit packaged navigation sample test passed
using the nested cache/staging layout. This path change does not alter its existing collection limits.
Failure feedback and manual reinstall remain future management UI work; no automatic repair or
additional integrity scanning is implemented. See the [package lifecycle design](01-Specification.md).

## Naming integration check (2026-10-21)

The renamed workspace was checked on Windows x64 with the current Modding assemblies and SDK:

- The Modding suite passed 95 cases, including source/directory SDK imports and real collectible fixtures.
- The selected Core modding, settings-migration and application-lifetime checks passed 26 cases.
  The migration fixture is now named `ModSettingsMigrationTests`.
- The separate Release patching suite passed four cases with `ModFixture.PatchA` and `ModFixture.PatchB`.
- The Windows source-host build and Everything package completed with zero warnings/errors on the final
  build. The explicit packaged Everything test passed, including the renamed entry, embedded icon,
  English/Chinese settings and two query/unload cycles.
- The navigation sample packaged through the host-directory workflow. It retains the known
  WindowsBase reference warning described below. Its explicit test passed AXAML loading, language
  switching, button behavior and contribution removal; the rendered Chinese page was inspected.
  Collection remained unconfirmed for `Everywhere.Mods.Sample`, so cleanup reported `RestartRequired`.

These checks use local Windows build outputs. Published SDK and cross-platform deployment gates
remain listed below.

## Current regression coverage

Version comparison and range tests live in Everywhere.Abstractions.Tests and exercise the shared
Everywhere.Versioning assembly. The historical fixture records the pre-change order of 88 local
Git tags and 10 settings migration declarations (91 distinct versions). It runs without Git and
checks ordering and distinct identities. Additional cases cover arbitrary prerelease labels,
numeric segment ordering, metadata-insensitive equality/hashing, normalization and interval bounds.
Core tests exercise pending migration selection from an existing Canary settings document and
verify that the external UpdateChannel enum retains the generated localization option keys.
SDK tests package a real prerelease mod, check exact prerelease API defaults and host range
rejection, then validate the generated manifest through the runtime ModDefinition path.
Stable defaults exclude the next major's prereleases with a -0 upper bound. Tool assemblies must
remain absent from generated mod archives.

Package localization tests use real author projects with both source and installation host imports.
They check omitted names and whole localization declarations, independent descriptions, Unicode README paths, content attachments,
convention discovery and explicit overrides, including Chinese-only README remapping and ambiguous English README rejection.
The generated maps are read back through ModPackageStore without loading mod code.
The same author builds verify generated and referenced satellites, explicit resource-copy metadata,
host resource exclusion, DLL-named ordinary content and rejection of a damaged managed output.
Both source-project and installation-directory imports use the host's generated resource manifest.
Everything packages built through both Windows source and directory host imports contain 13 name/
description languages and only English/Simplified Chinese README files. The maintained packaged
query/settings/unload test checks those maps and aliased generated settings keys before exercising
its two activation/retirement cycles.
DynamicLocaleKeyTests check case-insensitive language lookup, order-independent equality/hashing,
JSON and MessagePack round trips, and parent/English fallback. Text values remain case-sensitive.

Mod stop-reason tests exercise real collectible entries during hot unload, completed
initialization failure and ordinary exit, including a pending initializer. Cancellation callbacks and
entry disposal observe the assigned reason, and exit joining timed-out retirement keeps Unload.

The runtime separates registered reversal of application changes from optional entry disposal.
Normal DI disposal releases the engine and its entries without draining registered reversal containers.
No bounded managed-exit guarantee is made.

ChatPluginRegistrationTests and ChatPluginProxyTests cover admission closure, call/filter/stream
draining, dynamic discovery, complex parameters, default binding, service injection, aliases and
unchanged result values. Collectible functions and captured hooks must be collected while old chat
snapshots, results and closed registration handles remain reachable. Replacement registration must
not revive an old proxy. The fixture uses generated JSON metadata; unrelated reflective serializer
caches are outside that guarantee. SK's collectible parameter-converter keys use weak storage while
the converter factory and execution pipeline remain upstream code.

Reversible registration accepts ModChatPlugin only. The same-named built-in/mod regression checks
that built-in snapshots retain their ordinary function identity and remain callable after mod
retirement, while old mod functions are revoked. NativePlugins remains their shared UI list;
MCP keeps its separate connection lifetime.

ChatToolConsentTests exercise the production outer approval path with chat snapshots. They cover
MCP tool-group approval persistence, customized requests, null/allow/deny hook results, automatic
review and bypass precedence. The host applies rules by snapshot Key, not concrete ChatPlugin type.
Registration tests cover forwarding and revocation of the expanded hook.

The SK patch has also been validated from a published test output. This establishes patch copying
and execution for that output, not a full signed desktop release or macOS runtime validation.

Service-boundary tests resolve real `ILogger<ModType>` services directly, lazily and by key,
then retire one mod while another mod and the application root remain alive. Collection must
be confirmed before unloading the second mod. Additional lifecycle cases verify host singleton
identity through direct and collection resolution, scoped/transient behavior, constructor-injected
provider ownership, reactivation, and disposal after initialization failure. Service availability
checks use the native provider. The root's native relationship sources are marked per-scope so their
nested lookups cannot retain collectible types in the parent registry.

The maintained suite is `tests/Everywhere.Modding.Tests`. It exercises the current
Hosting, Packaging and SDK implementations.
Coverage includes optional method/base-type availability and consumer reconstruction,
controlled name-based assembly binding, initialization failure and retry, timeout handling,
retained host roots, entry-disposal/reversal failure, resource disposal contracts, and satellite
assembly resolution by metadata. Imported-entry cases cover a truncated DLL, a missing type and
an entry mistakenly pointing to a real satellite at an opaque path. They verify per-mod diagnostics,
blocked consumers and successful activation and retirement of an independent mod.
Failure-isolation cases use real collectible fixtures to verify
provider failure and transitive blocking, consumer failure with surviving siblings, independent
activation before and after failure, shared static registrations surviving a failed entry,
two-stage replacement without old-version resurrection, explicit removal despite an invalid related update,
sibling updates and new consumers of unchanged providers, shared-payload replacement ordering,
new content/binding conflicts against resident owners, and directed-cycle isolation. Disposal failure
and unconfirmed collection must prevent even preparing a new mod's assembly. A new native provider
cannot change a live resolver; an explicit replacement can retire its owner before selecting both providers. Package tests cover immutable installation copies, ZIP
paths, duplicate sources, broken-source isolation, user-data preservation and platform/version selection.
Case-colliding tags in name, description and README maps report DuplicateTranslation per source;
repairing the manifest permits retry while successful neighboring imports retain their installation IDs.

Resource tests cover synchronous and pending ValueTask sources, exactly-once result
consumption, asynchronous disposal preference, reverse sequential cleanup after failures,
and rejected registration. A collectible fixture registers real process-wide event
subscriptions from module and static initializers. Engine tests verify ownership before
initialization, shared assembly retention until the last activation stops, successful
collection and reload, cleanup after static-constructor/initializer failure, and retained
owners after cleanup failure or timeout. Completing a timed-out disposer does not clear
RestartRequired.

The WindowsBase reference-conflict warning remains an accepted limitation
of runtime-directory imports; the Everything source ProjectReference build no longer produces it.

Lifecycle cases verify optional absent/synchronous/asynchronous entry disposal, asynchronous
preference when both interfaces exist, repeated engine disposal, rejected later changes and
consumer-first exit. Exit preserves registrations and assembly owners. An entry-disposal failure
does not skip independent entries, and a reversal failure is not misclassified as disposal failure.
Exit joins incomplete initialization and a previously timed-out disposer without repeating disposal.
Cancellation reaches a still-running initializer before it is joined, allowing cooperative initialization
to finish without racing entry disposal.

A real buffered file writer is used by background work until Stopping is signaled. Hot unload,
completed initialization failure and normal exit all finish its final write and release the file
for exclusive access. Its reversal also reaches the shared release method; tests require one actual
release and verify that the reversal itself is skipped on exit. Hot/failure paths require confirmed
assembly collection, rather than treating RestartRequired as success.

Four SDK cases build independent author projects through directory and source imports,
with and without host implementation references. Source cases use real ProjectReference compilation,
including a transitive NuGet API. They check host-assembly/content/symbol exclusion, satellite output
paths, and isolation of command-line mod version/output overrides from the host and its transitive
projects. Host runtime-pack ownership is checked separately from compilation-reference selection.
Manifest tests cover omitted fields versus explicit null after source generation;
host-import tests cover declared-asset selection and invalid binding/path checks.

Run the maintained suite:

```powershell
dotnet test tests/Everywhere.Modding.Tests/Everywhere.Modding.Tests.csproj -c Debug
dotnet test tests/Everywhere.Modding.Tests/Everywhere.Modding.Tests.csproj -c Release
```

Real patch integration is opt-in through the separate
`tests/Everywhere.Modding.Patching.Tests` project. Two collectible fixture DLLs install hooks
on the same non-inlined host method. Three cases check both independent removal orders,
reinstallation, and initialization failure after a hook has become effective, followed by
cleanup and retry. Successful removal requires the engine's confirmed collection result;
`RestartRequired` is not accepted as successful unloading. RuntimeDetour 25.3.6 is a test
mod dependency only, not a dependency of the production host or SDK. Run this project in
a fresh test process; patch failures may leave that process unsuitable for further patching.

An exit-only case uses a separate target and deliberately retains its two hooks until the test process
ends. After entry disposal, calls still enter both hooks but safely pass through to the original method.
This verifies the distinction between stopping a mod instance and removing its registered patches.

```powershell
dotnet test tests/Everywhere.Modding.Patching.Tests/Everywhere.Modding.Patching.Tests.csproj -c Release
```

The Core navigation registration test verifies reversible UI contributions. Its separate,
explicit packaged-sample test renders compiled AXAML, switches English/Chinese, invokes a
button and verifies navigation/localization removal. It currently permits an unconfirmed
collection result and therefore is not an assertion of successful hot unload. See the
[sample instructions](../../mods/samples/Everywhere.Mods.Sample/README.md).
Actual desktop appearance, bootstrap/shutdown and published deployments require additional
validation; a headless test does not establish those results.

Engine disposal tests cover the engine's shutdown contract, rather than the application's native
dispatcher shutdown. Real Quit/Dock, logout, restart and shutdown remain platform validation gates;
automated tests must not terminate the user's session.

### Windows native loading and Everything

The maintained platform/native tests cover unsupported-platform skipping before foreign code is
inspected, optional availability, rejection of required skipped dependencies, and unused native
files that do not need to be loadable. A real private copy of Windows `version.dll` is resolved only
when invoked. Retirement must collect its managed owner and remove the module from the process.
Shared-provider tests cover different native bytes, one provider missing the asset, identical assets,
and reconstructing the remaining consumer when a provider disappears. Disagreement is checked at
the actual native request; no native dependency graph or eager preload is introduced.

The original native test showed that managed context collection alone left the DLL resident.
The implementation now owns the references acquired by its resolver and frees them only after
confirmed collection. This establishes the tested host path, not arbitrary native loader behavior.

The real `everywhere.everything` ZIP contains only Windows x64 SDK 2 and SDK 3 DLLs, bundled
Everything 1.5.0.1424b, its managed wrapper/entry, icon, license notice, dependency record and
12 satellite assemblies. It excludes host DLLs. A simulated `osx-arm64` selection reports enabled
but `Skipped` without running Windows code or checking Windows entry compatibility against macOS.
This is a selection test on Windows, not macOS native execution.

The explicit Core test starts a uniquely named 1.5 test instance and treats it as user-owned. It
queries a known file through the packaged chat tool, retains an old cloned function, and requires
successful unload/reload twice. The user process stays alive, Watchdog is never called, localization
registration disappears, later calls through the old function fail, and native package modules leave
the process. Select this explicit test by its exact fully qualified name; a combined NUnit filter
with ordinary tests can leave it unexecuted. See the [build/test instructions](../../mods/Everywhere.Mods.Everything/README.md).

A real collection failure was traced with ClrMD: retained display data held a FormattedDynamicLocaleKey,
which held a C# compiler-generated `IReadOnlyList` implementation defined in the mod. Its elements
were host-owned, but its container still retained the mod's loader allocator. Formatted and aggregate
keys now copy containers to arrays owned outside the mod. The packaged test passes while keeping
old chat/display snapshots reachable; clearing test history is not used as a workaround.

Reflection-based serialization can retain mod types through .NET accessor caches even when
function registration has been released. The host does not clear these internal caches or guarantee
arbitrary result graphs are collectible. Prefer generated serialization and host-owned persistent values.
Relevant runtime sources:
[options cache sharing](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/JsonSerializerOptions.Caching.cs),
[reflection accessor cache](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/ReflectionEmitCachingMemberAccessor.Cache.cs).

Independent client probes used the user's existing 1.4 default instance read-only and a test-owned
1.5 named instance. They checked bounded results/full paths/sizes, result validity after client disposal,
repeated queries and cancellation of an in-flight SDK 3 pipe read. SDK 3.0.0.9's viewport-count setter
sets its field but always returns FALSE; the wrapper ignores that incorrect return value for its valid state. Full path needs property 240,
and SDK 3 string lengths can include the terminator. These findings are reflected in the real wrapper.

The current wrapper also passed isolated 1.4.1.1028 default-instance checks and packaged
1.5.0.1424b named-instance checks. The legacy SDK 2 DLL was replaced with the official current
x64 binary after an identical query stalled with the old DLL and completed with the official one.
Both backends now distinguish IPC failure from a database that is still loading. SDK 2 retains
synchronous cancellation semantics; no managed window/message-loop adapter was introduced.
RID-less and win-x64 published NuGet consumers exercise the native assets. The packaged SDK 3
checks include independent disposal, copied results, cancellation and disconnection.

The mod now consumes the published EverythingNetCore 999.20261016.4 NuGet package,
including its license notice, instead of a sibling source project. Its packaged integration
test passed two query/unload/reload cycles with that release, including revocation of retained
tool functions, managed collection and native module release. Package restore used the configured
Sylinko feed. The source-import ProjectReference build now targets net10.0-windows10.0.19041.0
and completed with zero warnings/errors, selecting System.Reactive's NuGet `ref` assembly.
The resulting ZIP passed the same real query/unload test. Inspection confirmed that it contains
the mod, EverythingNet, its x64 native DLLs/executable, satellites, README, icon and license;
host managed/framework DLLs, symbols, XML and terminal helpers are absent. The mod suite passed
59 cases in Debug. Installation-directory imports retain the documented WindowsBase limitation.

Private startup exposed a service compatibility mistake: a running 1.4 pipe was initially accepted
for the bundled 1.5 process, whose database never became ready. The implementation now checks the
installed service executable version as well as its endpoint. The local 1.4 service is rejected before
launch/Watchdog registration. In a separate controlled probe, the test supplied a private folder-indexing
process to the production disposer; disposal stopped that process, left package files unchanged and
preserved the user's default instance. This does **not** validate the full successful private-startup
path with a compatible 1.5 service or elevated token. Those environments, and starting a stopped user
installation through discovery, remain manual acceptance cases. No service was installed or upgraded.
The upstream developer confirms the [1.4/1.5 service incompatibility](https://www.voidtools.com/forum/viewtopic.php?t=17050).

Both ordinary and win-x64 framework-dependent EverythingNetCore package consumption have been
validated; x86 native assets are excluded.

### Avalonia cleanup evidence and limits

On 2026-10-13, an isolated harness invoked the real packaged UI test with Avalonia 12.1.3,
returned from the test, and inspected the global property registry. Four strong paths
remained: `AvaloniaPropertyRegistry.Instance` -> `_properties` -> `Background`,
`ClipToBounds`, `IsVisible` or `Template` -> `_metadataCache` keys -> the mod's
`SamplePage` type. The sample does not declare new Avalonia properties; inherited-property
metadata lookup is enough to create these roots.

Each comparison ran in a fresh process. Additional collection checks observed weak
references to both the mod `Assembly` and its `AssemblyLoadContext`:

| Additional action after normal UI/resource cleanup | Assembly / context collected |
|---|---|
| None | No / no |
| Only `AssetLoader.InvalidateAssemblyCache` for the sample | No / no; the four property-cache paths remained |
| Only `AvaloniaPropertyRegistry.Instance.UnregisterByModule` for the sample types | Yes / yes; repeated with the same result in another fresh process |

This confirms the current sample's cause and an effective public cleanup operation. No
private framework cache was mutated. The probe called the API after the engine had already
reported `RestartRequired`; it does not validate production ordering, permit resetting that
engine state, or establish safe repeated in-process replacement. The production integration
must record and reverse the relevant framework registrations before its collection verdict.
A general full-type scan is no longer the agreed approach; the probe's GetTypes call is
suitable for this fixed fixture only.

## Historical findings and their limits

The early feasibility projects, repeated observation logs, publish inventories and timing
harnesses have been retired. The following are historical observations, not current test
results or promises about every runtime/library version:

- Collectible generic instantiations showed creation-order-sensitive lifetime relationships.
  Dependency-first owner creation remains the policy, together with actual collection checks.
  It does not guarantee independent unload for arbitrary dynamic loading or generic use.
- Lib.Harmony 2.4.3 experiments found exception-filter reconstruction and callback-retention
  problems. RuntimeDetour 25.3.6 worked for the tested ordinary methods and exception handlers;
  its Hook constructor rejected generic method targets. No universal generic patch workaround
  was established. Mod authors own patch handles and cooperative callback shutdown.
- Optional method calls and derived types can remain dormant until declared dependencies are
  available. Changing availability may require stopping and reconstructing the whole consumer.
  The maintained fixture covers this policy, not every possible JIT trigger or third-party loader.
- Name-based binding can be mediated by the requester context. Arbitrary file loading and
  framework/global caches remain outside that guarantee; interpreted expressions were among
  the historical cases retaining collectible code. Restart remains the fallback.
- Small SDK import experiments supported using declared host dependency information without
  requiring strong-name signatures or PreserveCompilationContext. Formal SDK tests now cover
  the ordinary directory/source workflows. Cross-platform published imports remain unverified.
- Earlier publish-size comparisons informed disabling trimming and adopting Rx 7. Their
  measurements depended on particular builds and are not a current size budget.

Shared-generic assembly ownership across independent mods and concurrent/reentrant application
operations still need focused current-implementation coverage. The generic service tests above cover
DI registrations, not every cross-mod CLR generic relationship.

## Known issues

The Avalonia UI unload issues below are open as of 2026-10-13. Runtime observations use
Avalonia 12.1.3; source review uses commit `38402fc046e7f56298a7c9cedd51600e1ff6f9e0`.
An observed reference path is not the same as a completed collectible-fixture test.
Recording these limits does not prohibit custom controls or claim that every UI mod
leaks. If required collection cannot be confirmed, keep `RestartRequired` and use a
process restart. Do not silently label these cases as successful hot unloads.

### Host property cleanup is not integrated

**Evidence: reproduced; public-API cleanup verified in an isolated probe.** The current
production path leaves inherited-property metadata caches holding the sample's type.
`UnregisterByModule` releases these roots in the probe, but the host has not integrated
it before its collection verdict. Current handling remains `RestartRequired`.

**To close:** validate framework registration tracking and reversal, then test ordinary removal,
replacement, completed initialization failure, shared assembly owners, unavailable
optional dependencies and repeated activation. Keep type discovery compatible with lazy
optional code. The probe's unrestricted `GetTypes()` is not a production solution.

### Custom properties and additional owners remain registered

**Evidence: source-confirmed reference paths; dedicated unload fixtures pending.**
`UnregisterByModule` removes per-type metadata and four lookup caches, but not owner keys
from the registered/attached/direct maps or property objects from `_properties`.
New styled, attached and direct properties can therefore retain mod types through
`OwnerType`, `PropertyType`, defaults or callbacks. Direct properties also retain typed
getters/setters. `AddOwner<ModControl>()` leaves an owner-type key even when reusing
an existing property object.

**Current handling and follow-up:** do not promise collection for these patterns. Add
separate fixtures for new properties and `AddOwner`, then assess a supported cleanup path
or an upstream fix. The current method's successful return does not establish that these
registrations were removed. No private-table mutation is approved as a generic workaround.

### Class handlers and layout/render helpers retain subscriptions

**Evidence: source-confirmed ownership; dedicated unload fixtures pending.** Property
unregistration does not unsubscribe `Changed` observers or class handlers. A surviving
property can retain the mod's callback or generic control type. `AddClassHandler`
returns an `IDisposable`; register that responsibility with the mod activation.
A static field inside the mod is not inherently a leak: the relevant root is the
surviving framework observable holding its subscription.

`AffectsMeasure<T>`, `AffectsArrange<T>` and `AffectsRender<T>` install subscriptions but
do not return the handles in the inspected implementation. These common helpers need
separate treatment; a developer cannot simply dispose a token the API never exposes.

**Current handling and follow-up:** dispose exposed subscriptions, preserve restart
fallback for unconfirmed collection, and test both explicit handlers and each helper.
Determine whether supported alternatives or upstream changes are needed for hidden
subscriptions before claiming custom-control hot-unload support.

### Custom routed events use a separate global registry

**Evidence: source-confirmed reference path; dedicated unload fixture pending.**
`RoutedEvent.Register(...)` stores owner types and event objects in `RoutedEventRegistry`.
The inspected registry has no corresponding unregister operation. Property cleanup does
not touch it. Registering a new event differs from attaching a removable handler to an
existing event.

**Current handling and follow-up:** dispose handlers that expose tokens, but do not treat
that as removal of custom event definitions. Verify the retained assembly with a minimal
fixture and assess framework support for unregistering the definitions. Restart remains
the fallback when collection fails.

### Mutations keyed by host types cannot be inferred from mod types

**Evidence: source-reviewed ownership limitation; dedicated fixtures pending.** A metadata
override on a host type is keyed by that host type, even if a mod supplied its callback
or value. Removing entries keyed by mod types cannot automatically reverse this change.
Clearing host types globally could affect surviving UI and other mods.

**Current handling and follow-up:** require explicit ownership and a supported reversal
for host mutations; otherwise retain the restart requirement. Test a concrete override
before documenting it as reversible. Do not unregister shared host types as a workaround.

### Other UI resources and third-party controls need component-specific validation

**Evidence: unresolved validation coverage, not a demonstrated leak in every component.**
Application styles/resources, bindings, animations and third-party controls can introduce
lifetimes outside the property registry. The current sample does not cover all of them,
and no general cleanup operation has been validated for these resources.

**Current handling and follow-up:** remove mod-owned contributions and dispose exposed
resources, then validate the actual component and repeated replacement in the desktop
host. Investigate surviving references individually. Preserve the distinction between
completed UI removal and confirmed assembly collection.

Relevant sources: [property registry](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/AvaloniaPropertyRegistry.cs),
[property metadata](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/AvaloniaProperty.cs),
[styled-property owners](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/StyledProperty.cs),
[direct properties](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/DirectProperty.cs),
[class handlers](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/AvaloniaObjectExtensions.cs),
[layout helpers](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/Layout/Layoutable.cs),
[render helpers](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/Visual.cs), and
[routed-event registry](https://github.com/AvaloniaUI/Avalonia/blob/38402fc046e7f56298a7c9cedd51600e1ff6f9e0/src/Avalonia.Base/Interactivity/RoutedEventRegistry.cs).

## Mod settings

**Evidence: maintained tests against the generic settings engine, real collectible activations and
Avalonia headless settings views.** ModSettingsTests loads separate copies of the fixture assembly
through ModEngine. The fixture uses generated JSON and settings descriptors, GeneratedSettingsItems,
a mod enum dictionary key and a mod-owned converter for System.Int32 keys.

- Two activations use isolated documents and converter options. Settings UI is lazy, its view is stable,
  and edits reach the same model. Hot retirement detaches items, saves final changes and confirms
  collection even while the host retains old views and closed registration handles. A closed old handle
  cannot remove a replacement's registration.
- A migration that has committed is not repeated after a later initialization failure. Invalid model
  data fails activation without replacing the file. Failed activations must collect, rather than
  passing by requesting restart.
- Process exit saves with an open view while the test UI thread cannot service dispatcher work.
  This checks disposal beginning as Exit; it does not prove every native desktop quit sequence or an
  exit overlapping an already-started hot stop. The application consumes settings views in the chat
  tool page, but calls ApplyAsync only during startup. A future hot-reload entry point must coordinate
  outstanding UI retirement before dispatcher shutdown.
- ModSettingsMigrationTests checks ordered migration, delayed asynchronous disposal before commit,
  borrowed-service ownership, first-run/current-version constructor skipping, invalid/newer-version
  rejection and original-file preservation after migration, validation or disposal failure.
- The application SettingsInitializer and migration regressions remain covered alongside these tests.

The mounted-editor regression uses the production SettingsItemsControl and SettingsItemControl
templates in an open headless window. A real ToggleSwitch updates the collectible model through
the generated settings items. Hot removal must remove the editor, save the changed value and
confirm collection while the host still retains the window and settings view.

ChatPluginPageViewModelTests covers selection changes, worker-thread removal after UI delivery,
and page disposal without any packaged mod or native runtime. It retains old tabs and bound host
views, exercises the real editor template, and checks that retaining the catalog does not keep a
disposed page alive through its collection-change subscription. These checks do not make
registration disposal a completion barrier for asynchronous UI delivery.

EverythingModTests exercises the packaged mod and chat-page view model against two isolated
Everything 1.5 instances with different indexes. It edits both generated text settings, checks the
same chat snapshot queries the newly selected index, verifies automatic saving before unload and
saved values after reload, and retains a host control bound to the original SettingsTabItem during
collection. Everything exposes ChatPlugin.SettingsItems; the test services deliberately contain no
IModSettingsRegistry. Tool removal clears the page selection/tabs and the tab's observable item
source clears the retained control's binding. Old calls are revoked, and both managed
collection and native-module release are required. Both test-owned user instances survive mod
unload and never enter Watchdog. This is headless integration coverage, not desktop visual acceptance.

The SDK package no longer includes or imports Everywhere.Configuration.SourceGenerator. Everything
opts into the analyzer explicitly through ProjectReference; its runtime ZIP excludes the generator
and host-owned assemblies. The Windows source-host build and package check complete without a
WindowsBase warning. The general mod management page remains deferred.

The temporary migration provider uses standard MSDI disposal. A disposal exception stops remaining
service disposal; the test verifies that no migrated document is committed in that case. This is not
aggregate-and-continue cleanup. See the [.NET implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ServiceProviderEngineScope.cs).

Mod-model bindings in the maintained UI path are generated compiled bindings; Everything's retained
test control binds only to the host-owned SettingsTabItem. A reflection Binding comparison against a mod model retained
the assembly, but the precise framework root is not diagnosed. No general Avalonia cache cleanup or
management page is implemented. The author remains responsible for custom UI resources and the host
still requires confirmed collection.

## Remaining release gates

- Complete native desktop exit validation and broader UI/patch callback-draining coverage. Optional
  entry disposal, owned IO, partial initialization, shared release and ordinary DI ownership have
  regression coverage; no headless result proves every native quit path or arbitrary mod lifetime.
- Validate Avalonia registration tracking/reversal before collection checks, including repeated replacement and the
  separate registration patterns described above.
- Verify the released SDK resolver/package, actual published-host imports, cross-platform
  entry selection and the complete author/agent workflow.
- Validate interrupted package installation, update/restart handoff and deletion failures
  against the final application integration.
- Validate Windows and macOS published deployments, including platform signing/runtime
  constraints and mod-owned patch behavior.
- Close current graph/lifetime coverage gaps and assess representative long-running workloads.
  Historical microbenchmarks do not establish a startup, memory or unload-latency guarantee.
- Validate Everything private startup with a compatible 1.5 service/elevated token and starting a stopped
  user installation; retain the explicit package test for user-process preservation. Extend native asset
  ownership and recovery validation to further libraries and platforms before claiming their support.

Unconfirmed collection requires `RestartRequired`; it must not be reported as a successful
hot unload. Failed historical cases must not be converted into unsupported compatibility claims.
