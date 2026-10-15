# Plugin validation

## Current regression coverage

The current runtime separates registered reversal of application changes from optional entry
disposal. IPlugin.ShutdownAsync and IAsyncShutdown have been removed. Normal DI disposal releases
the engine and its entries, without draining registered reversal containers. The former fixed exit
budget and ShutdownDiagnostics API are removed; no bounded managed-exit claim is made.

On 2026-10-14, the complete Release suite passed all 65 cases, including the pending-initialization
cancellation regression. The focused lifecycle/resource run passed all 30 cases. The four real RuntimeDetour
cases passed in Release, and four focused Core application/DI/navigation cases passed in Debug.
Windows and macOS entry projects passed managed compilation with zero warnings/errors using existing
dependency outputs (`Compile`, `BuildProjectReferences=false`, `DesignTimeBuild=true`). This is not a
full publish or native platform execution result.

The maintained suite is `tests/Everywhere.Plugin.Tests`. It exercises the current
Hosting, Packaging and SDK implementations, rather than the retired experimental loader.
Coverage includes optional method/base-type availability and consumer reconstruction,
controlled name-based assembly binding, initialization failure and retry, timeout handling,
retained host roots, entry-disposal/reversal failure, resource disposal contracts, and satellite
assembly resolution by metadata. Package tests cover immutable installation copies, ZIP
paths, duplicate sources, user-data preservation and platform/version selection.

Resource tests cover synchronous and pending ValueTask sources, exactly-once result
consumption, asynchronous disposal preference, reverse sequential cleanup after failures,
and rejected registration. A collectible fixture registers real process-wide event
subscriptions from module and static initializers. Engine tests verify ownership before
initialization, shared assembly retention until the last activation stops, successful
collection and reload, cleanup after static-constructor/initializer failure, and retained
owners after cleanup failure or timeout. Completing a timed-out disposer does not clear
RestartRequired.

The Windows Debug host also completed a full build with zero errors and eight existing
Watchdog AOT/trimming warnings. The UI sample's Register call was migrated, but repackaging
is currently blocked by an unrelated missing HandleClick method: SamplePage.axaml still
references it while SamplePage.axaml.cs does not define it (AVLN3000). The existing
WindowsBase reference-conflict warning also remains. The page was not changed in this
resource-lifetime work, and no new packaged UI result is claimed.

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
with and without host implementation references. They check host-assembly exclusion and
satellite output paths. The CLI check test verifies machine-readable success/failure output.
Manifest and host-import tests retain format boundaries and asset-selection checks, without
requiring JSON property order or inspecting the source catalog's private index layout.

Run the maintained suite:

```powershell
dotnet test tests/Everywhere.Plugin.Tests/Everywhere.Plugin.Tests.csproj -c Debug
dotnet test tests/Everywhere.Plugin.Tests/Everywhere.Plugin.Tests.csproj -c Release
```

Real patch integration is opt-in through the separate
`tests/Everywhere.Plugin.Patching.Tests` project. Two collectible fixture DLLs install hooks
on the same non-inlined host method. Three cases check both independent removal orders,
reinstallation, and initialization failure after a hook has become effective, followed by
cleanup and retry. Successful removal requires the engine's confirmed collection result;
`RestartRequired` is not accepted as successful unloading. RuntimeDetour 25.3.6 is a test
plugin dependency only, not a dependency of the production host or SDK. Run this project in
a fresh test process; patch failures may leave that process unsuitable for further patching.

An exit-only case uses a separate target and deliberately retains its two hooks until the test process
ends. After entry disposal, calls still enter both hooks but safely pass through to the original method.
This verifies the distinction between stopping a plugin instance and removing its registered patches.

```powershell
dotnet test tests/Everywhere.Plugin.Patching.Tests/Everywhere.Plugin.Patching.Tests.csproj -c Release
```

The Core navigation registration test verifies reversible UI contributions. Its separate,
explicit packaged-sample test renders compiled AXAML, switches English/Chinese, invokes a
button and verifies navigation/localization removal. It currently permits an unconfirmed
collection result and therefore is not an assertion of successful hot unload. See the
[sample instructions](../../plugins/samples/Everywhere.Plugin.Sample/README.md).
Actual desktop appearance, bootstrap/shutdown and published deployments require additional
validation; a headless test does not establish those results.

The application checks in `tests/Everywhere.Core.Tests/Common/ApplicationShutdownTests.cs` verify
that App.Shutdown closes the lifetime without a pre-exit service phase, the existing entry-point
startup drain remains separate, and normal DI disposal invokes the initializer service's owned
engine. A startup coordinator regression blocks the UI thread after cancellation and verifies that its
pending initializer can complete without another UI continuation. The focused navigation test still
verifies removal of a selected contribution. These checks
do not simulate a complete native dispatcher shutdown or establish OS session-termination behavior.
The macOS delegate still routes ordinary Quit through Avalonia and allows system-classified termination
through. Real Quit/Dock, logout, restart and shutdown remain platform validation gates; automated tests
must not terminate the user's session.

### Avalonia cleanup evidence and limits

On 2026-10-13, an isolated harness invoked the real packaged UI test with Avalonia 12.1.3,
returned from the test, and inspected the global property registry. Four strong paths
remained: `AvaloniaPropertyRegistry.Instance` -> `_properties` -> `Background`,
`ClipToBounds`, `IsVisible` or `Template` -> `_metadataCache` keys -> the plugin's
`SamplePage` type. The sample does not declare new Avalonia properties; inherited-property
metadata lookup is enough to create these roots.

Each comparison ran in a fresh process. Additional collection checks observed weak
references to both the plugin `Assembly` and its `AssemblyLoadContext`:

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

The temporary UI diagnosis remains at
`tests/Everywhere.Plugin.Probes/artifacts/ui-unload` until production cleanup is integrated.
Its performance measurements are not part of the supported behavior or a release threshold.

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
  was established. Plugin authors own patch handles and cooperative callback shutdown.
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

The deleted pre-rewrite engine suite is not coverage of today's engine. In particular,
directed-cycle rejection, valid diamonds, shared-generic independent collection, dependency
replacement and concurrent/reentrant application need current-implementation evidence before
being described as continuously verified. Do not restore an entire prototype solely to
preserve its historical test count; add focused regression cases when closing these gaps.

## Known issues

The Avalonia UI unload issues below are open as of 2026-10-13. Runtime observations use
Avalonia 12.1.3; source review uses commit `38402fc046e7f56298a7c9cedd51600e1ff6f9e0`.
An observed reference path is not the same as a completed collectible-fixture test.
Recording these limits does not prohibit custom controls or claim that every UI plugin
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
New styled, attached and direct properties can therefore retain plugin types through
`OwnerType`, `PropertyType`, defaults or callbacks. Direct properties also retain typed
getters/setters. `AddOwner<PluginControl>()` leaves an owner-type key even when reusing
an existing property object.

**Current handling and follow-up:** do not promise collection for these patterns. Add
separate fixtures for new properties and `AddOwner`, then assess a supported cleanup path
or an upstream fix. The current method's successful return does not establish that these
registrations were removed. No private-table mutation is approved as a generic workaround.

### Class handlers and layout/render helpers retain subscriptions

**Evidence: source-confirmed ownership; dedicated unload fixtures pending.** Property
unregistration does not unsubscribe `Changed` observers or class handlers. A surviving
property can retain the plugin's callback or generic control type. `AddClassHandler`
returns an `IDisposable`; register that responsibility with the plugin activation.
A static field inside the plugin is not inherently a leak: the relevant root is the
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

### Mutations keyed by host types cannot be inferred from plugin types

**Evidence: source-reviewed ownership limitation; dedicated fixtures pending.** A metadata
override on a host type is keyed by that host type, even if a plugin supplied its callback
or value. Removing entries keyed by plugin types cannot automatically reverse this change.
Clearing host types globally could affect surviving UI and other plugins.

**Current handling and follow-up:** require explicit ownership and a supported reversal
for host mutations; otherwise retain the restart requirement. Test a concrete override
before documenting it as reversible. Do not unregister shared host types as a workaround.

### Other UI resources and third-party controls need component-specific validation

**Evidence: unresolved validation coverage, not a demonstrated leak in every component.**
Application styles/resources, bindings, animations and third-party controls can introduce
lifetimes outside the property registry. The current sample does not cover all of them,
and no general cleanup operation has been validated for these resources.

**Current handling and follow-up:** remove plugin-owned contributions and dispose exposed
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

## Remaining release gates

- Complete native desktop exit validation and broader UI/patch callback-draining coverage. Optional
  entry disposal, owned IO, partial initialization, shared release and ordinary DI ownership have
  regression coverage; no headless result proves every native quit path or arbitrary plugin lifetime.
- Validate Avalonia registration tracking/reversal before collection checks, including repeated replacement and the
  separate registration patterns described above.
- Verify the released SDK resolver/package, actual published-host imports, cross-platform
  entry selection and the complete author/agent workflow.
- Validate interrupted package installation, update/restart handoff and deletion failures
  against the final application integration.
- Validate Windows and macOS published deployments, including platform signing/runtime
  constraints and plugin-owned patch behavior.
- Close current graph/lifetime coverage gaps and assess representative long-running workloads.
  Historical microbenchmarks do not establish a startup, memory or unload-latency guarantee.
- Validate native asset ownership and application recovery behavior where support is claimed.

Unconfirmed collection requires `RestartRequired`; it must not be reported as a successful
hot unload. Failed historical cases must not be converted into unsupported compatibility claims.
