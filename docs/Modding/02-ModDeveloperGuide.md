# Mod Developer Guide

This guide covers building, structuring, and unloading an Everywhere mod safely.

## 1. Project Setup

Import the workspace SDK (until published to NuGet) in your `.csproj`:

```xml
<Project>
  <Import Project="../../src/Everywhere.Modding.Sdk/Sdk/Sdk.props"/>

  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <Version>1.0.0</Version>
    <EverywhereModToolsDirectory>$(MSBuildThisFileDirectory)../../src/Everywhere.Modding.Sdk/bin/$(Configuration)/net11.0</EverywhereModToolsDirectory>
    <EverywhereModId>example.mymod</EverywhereModId>
    <EverywhereModEntryType>Example.MyMod.MyModEntry</EverywhereModEntryType>
    <!-- For platform-specific mods, match RuntimeIdentifier and EverywhereModPlatform, e.g. win-x64 -->
    <EverywhereI18NNamespace>Example.MyMod.I18N</EverywhereI18NNamespace>
  </PropertyGroup>

  <Import Project="../../src/Build.I18N.targets"/>
  <Import Project="../../src/Everywhere.Modding.Sdk/Sdk/Sdk.targets"/>
</Project>
```

Copy `Everywhere.local.props.example` to `Everywhere.local.props` (gitignored) when targeting a built host directory instead of a project reference. Note: when referencing `EverywhereHost` as a `.csproj` (e.g., `Everywhere.Windows.csproj`), your mod project must use a compatible TFM (such as `net11.0-windows10.0.19041.0`).

Build and package into a distributable `.zip`:

```powershell
dotnet build src/Everywhere.Modding.Sdk/Everywhere.Modding.Sdk.csproj
dotnet build mods/Everywhere.Mods.Everything/Everywhere.Mods.Everything.csproj -t:EverywherePack
```

Add `<EverywhereModLocalization Include="en">...</EverywhereModLocalization>` items for localized manifest metadata (`Name`, `Description`, `Readme`).

## 2. Entry Point and Host Resources

Implement `IMod` (and `IAsyncDisposable` if cleanup is async):

```csharp
public sealed class MyMod : IMod, IAsyncDisposable
{
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        context.Register(
            LocaleManager.RegisterProvider(
                typeof(MyMod).Assembly,
                static () => new ResourceManagerLocaleResourceProvider(
                    "Example.MyMod.I18N.Strings",
                    typeof(MyMod).Assembly)));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Key ownership rules:

- Register host-side contributions via `context.Register(...)` so unloading revokes them automatically.
- Dispose private resources (processes, sockets, watchdog registrations, `SettingsEngine<T>`) cleanly in `DisposeAsync`.
- During `ModStopReason.Exit`, avoid awaiting `Dispatcher.UIThread` because the UI loop may already have exited; dispatch UI teardown only when `context.StopReason != ModStopReason.Exit`.

## 3. Native Libraries and Mutable Data

- Treat `IModContext.InstallationDirectory` as **read-only** (it sits under the package cache and may be retired).
- Store writable state, downloaded binaries, configs, and databases under `IModContext.DataDirectory`.
- Bundled native binaries placed under `runtimes/{rid}/native/` are tracked by `NativeLibraryHandles` and released only after collectible ALC confirmation so Windows file locks can be cleaned cleanly.

## 4. Collectible Memory Warnings (Avoiding Unload Pinning)

Every mod loads in a collectible `AssemblyLoadContext`. Failing to release host-to-mod references causes `ModEngine` to report `ModApplyStatus.RestartRequired` (`CollectionUnconfirmed`):

1. **Collections crossing host boundaries**: `DynamicLocaleKey` formats arguments (`Args`), `FormattedDynamicLocaleKey`, and `AggregateDynamicLocaleKey` convert incoming arrays/enumerables defensively (`Type.IsCollectible` guard), but **author code must still avoid returning mod-defined collection types, custom `IEnumerator` state machines, or mod subtypes to long-lived host caches**. Convert or materialize results to host-owned types (arrays, strings, host DTOs) before handing them to long-lived UI or chat models.
2. **Event handlers and static caches**: Always unsubscribe from host events (`PropertyChanged`, `CollectionChanged`, `AppDomain` events) during disposal, and avoid registering mod types into process-wide static caches (e.g., global `JsonSerializerOptions` or custom reflection dictionaries). Use `[JsonSerializable]` source-generated `JsonSerializerContext` for mod-owned models.
3. **Host service lifetimes**: Never resolve mod instances as host singletons; obtain host services through `IModContext.Services`, and never dispose host singletons from mod code.
4. **Avalonia controls and custom properties**: Avalonia retains static metadata for registered `StyledProperty` / `DirectProperty` and class handlers in global registries unless explicitly cleaned up. Custom AXAML pages should expect `RestartRequired` until Avalonia metadata cleanup is automated host-wide. See [03 — Validation](03-Validation.md#avalonia-cleanup-limits).
