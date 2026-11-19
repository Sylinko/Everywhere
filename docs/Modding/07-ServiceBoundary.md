# Service Boundary (Autofac & Lifetime Ownership)

**Status:** implemented via `HostServiceContainer` (`Autofac` 9.3.4 + `Autofac.Extensions.DependencyInjection` 11.0.2).

## 1. Application and Activation Ownership

- The host registers services in `IServiceCollection` and builds `HostServiceContainer` (`Everywhere.Modding.Hosting.Services`).
- `AddModding()` registers `IModSettingsRegistry` (`ModSettingsRegistry`) and `ModEngineManager` (`AddAsyncInitializer<ModEngineManager>()`).
- Initializers use static ordering metadata (`IAsyncInitializer.Index`) so the startup coordinator resolves each group sequentially without prematurely constructing later-phase services.
- Each mod activation gets an isolated child lifetime scope tied to its entry assembly's collectible `AssemblyLoadContext`, exposed to mods via standard DI (`IModContext.Services`). Mod code never references Autofac directly and cannot mutate the global service container.

## 2. Registration Scope Rules (`HostRegistrationSources`)

- **Host singletons** remain application-owned across direct, collection, and constructor injection; mod activation disposal never disposes host singletons.
- **Scoped and transient services** keep their declared lifetimes inside the activation scope.
- **Open generic registrations** (such as `ILogger<TModType>`) closed over collectible mod types receive activation-owned closed instances, preventing collectible types from leaking into root container caches.
- Child scopes created inside an activation inherit those boundary rules.

## 3. Retirement and Autofac Reflection Cache Cleanup

- When a mod unloads, its activation scope is disposed cleanly before `AssemblyRegistry.CleanupAsync` runs.
- `ModReflectionCache.Clear` removes entries in `Autofac.Core.ReflectionCacheSet.Shared` whose keys, generic type arguments, element types, declaring types, or recorded assemblies belong to any retiring `AssemblyLoadContext`, without clearing unrelated live mods or host entries.
