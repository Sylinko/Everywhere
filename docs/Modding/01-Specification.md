# Modding Architecture and Specification

**Status:** implemented in `Everywhere.Modding.Abstractions`, `Everywhere.Modding.Hosting`, `Everywhere.Modding.Packaging`, and `Everywhere.Modding.Sdk` on `.NET 11`.

## 1. Projects and Identities

| Assembly | Role |
|---|---|
| `Everywhere.Modding.Abstractions` | Mod contracts (`IMod`, `IModContext`, `ModId`, `ModState`, `ModInfo`, `ModDiagnostic`) |
| `Everywhere.Modding.Packaging` | Package manifest (`mod.json`), host descriptor (`mod-host.json`), validation, and ZIP store (`ModPackageStore`) |
| `Everywhere.Modding.Hosting` | Dependency graph (`ModLoadPlan`), collectible `AssemblyLoadContext` registry (`AssemblyRegistry`), Autofac boundary (`HostServiceContainer`), and `ModEngine` |
| `Everywhere.Modding.Sdk` | MSBuild props/targets (`Sdk.props`, `Sdk.targets`, `EverywherePack`) and `EverywhereExportModHost` CLI tool |

- **Mod identity (`EverywhereModId` / `ModId`)**: stable lowercase dotted identifier (for example `everywhere.everything`, `everywhere.sample.navigation`).
- **Platform selection (`EverywhereModPlatform`)**: runtime identifier such as `universal` or `win-x64`. Unsupported platform variants enter `ModState.Skipped` without error.
- **Host descriptor (`mod-host.json`)**: exported beside the host binary (`Build.Modding.Hosting.targets`) declaring host version, API version, target framework, and shared host assembly identities (`HostAssemblyCatalog`).

## 2. Loading, Isolation, and Dependencies

- Mod assemblies are inspected via metadata before loading into collectible `AssemblyLoadContext` instances (`AssemblyOwner`).
- Host-declared assemblies resolve strictly to `AssemblyLoadContext.Default` via `HostAssemblyCatalog`, guaranteeing shared contract identity (`IMod`, `Everywhere.Abstractions`, `Everywhere.Core`, `Microsoft.Extensions.AI`, `Avalonia`, etc.).
- Mod-private managed assemblies (such as `EverythingNetCore`) and native libraries (`runtimes/{rid}/native/*` and `NativeLibraryHandles`) are owned by the mod's collectible context and must not leak into `AssemblyLoadContext.Default`.
- **Optional dependencies**: `IModContext.IsModUsable(ModId)` and `ModLoadPlan` track dependency availability. Code referencing optional dependency types must stay isolated behind non-inlined helper methods guarded by usability checks.

## 3. Lifecycle and Cleanup

1. `ModEngineManager` runs at startup via `IAsyncInitializer`, imports packages from `~/.everywhere/mods/` (both `.zip` archives and extracted folders containing `mod.json`) into `%APPDATA%/Everywhere/cache/mods/packages/`, and applies enabled mods through `ModEngine.ApplyAsync`.
2. Each active mod gets a scoped `HostServiceContainer` lifetime and `IModContext` (`ModEngine` constructs `IMod`, invokes `InitializeAsync(context, cancellationToken)`).
3. `context.Register(...)` records reversible host registrations (localization providers, navigation items, chat plugin registrations, settings views) that are disposed automatically when the activation stops.
4. On hot unload (`ModStopReason.Unloaded`) or reload:
   - `IMod` is disposed asynchronously (`DisposeAsync` / `Dispose`), followed by registered resources and the Autofac child scope.
   - `AssemblyRegistry.CleanupAsync` runs static assembly cleanup (`ModResource`) and `ModReflectionCache.Clear` removes Autofac reflection entries referencing retiring `AssemblyLoadContext`s.
   - `ModEngine.ReleaseAsync` runs bounded GC collection attempts (`CollectionAttempts`, `CollectionDelay`). If every retiring context is collected, native handles are released and the operation returns `ModApplyStatus.Applied`; otherwise `ModApplyStatus.RestartRequired` is returned with a `CollectionUnconfirmed` diagnostic listing uncollected owners.
5. On process exit (`ModStopReason.Exit`), disposal drains async shutdown without blocking on a stopped UI dispatcher or running GC collection confirmation loops.

## 4. Packaging and Storage Layout

- **Source directory**: `~/.everywhere/mods/` holds user-installed `.zip` packages or unpacked mod directories. Do not place both a `.zip` and an unpacked folder for the same mod ID simultaneously.
- **Cache directory**: `%APPDATA%/Everywhere/cache/mods/packages/` (imported packages) and `staging/` (temporary staging). Safe to delete after exit; rebuilt automatically on next launch.
- **Persistent mod data**: `~/.everywhere/mods-data/{id}/` (`IModContext.DataDirectory`), preserved across updates and uninstalls.
