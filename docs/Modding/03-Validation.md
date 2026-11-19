# Modding Validation and Known Limits

**Environment:** `.NET 11` (`net11.0` / `net11.0-windows10.0.19041.0`), Harness + `Microsoft.Extensions.AI` tool pipeline, Autofac 9.3.4 service boundary.

## 1. Automated Test Coverage

| Suite | Scope | Command |
|---|---|---|
| `Everywhere.Abstractions.Tests` | Core types, localization key collectible safety | `dotnet test tests/Everywhere.Abstractions.Tests/Everywhere.Abstractions.Tests.csproj` |
| `Everywhere.Modding.Tests` | Manifest parsing, validation, packaging, host reference sets, dependency graph, ALC isolation & disposal, native library release | `dotnet test tests/Everywhere.Modding.Tests/Everywhere.Modding.Tests.csproj` |
| `Everywhere.Core.Tests` (`Modding`) | `ModSettingsTests`, `ModNavigationTests`, `ChatPluginPageViewModelTests`, `ChatPluginRegistrationTests`, `ChatPluginProxyTests`, `EverythingModTests` | `dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter "FullyQualifiedName~Modding"` |
| `Everywhere.Core.Tests` (`Configuration`) | `SettingsInitializerTests`, `SettingsMigratorTests`, `ModSettingsMigrationTests`, migration suites | `dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter "FullyQualifiedName~Configuration"` |

> Note: The former `Everywhere.Modding.Patching.Tests` experimental suite is omitted on `.NET 11` because `MonoMod.RuntimeDetour 25.3.6` does not yet support CoreCLR 11 runtime detours (`PlatformNotSupportedException`).

## 2. Explicit Packaged End-to-End Integration Checks

### `EverythingModTests` (Windows Everything 1.5a + Harness + Native Unload)

Verifies packaged `everywhere.everything-1.0.0.zip`: skipping incompatible platforms, dual-round hot load/unload against isolated Everything 1.5a instances, `ToolBinding` query execution before and after live settings edits, debounced `settings.json` persistence, old binding revocation after unload, `ChatPluginPageViewModel` settings tab detachment, watchdog non-interference for user processes, and **confirmed ALC + `EverythingNet` + native binary unload** (`ModApplyStatus.Applied`).

```powershell
dotnet build Everywhere.Windows.slnx
dotnet build mods/Everywhere.Mods.Everything/Everywhere.Mods.Everything.csproj -t:EverywherePack
$env:EVERYWHERE_EVERYTHING_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net11.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_EVERYTHING_PACKAGE = (Resolve-Path 'mods/Everywhere.Mods.Everything/bin/Debug/net11.0-windows10.0.19041.0/win-x64/everywhere.everything-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --no-build --filter 'FullyQualifiedName~PackagedEverything_QueryAndUnload_PreservesUserProcessAndRevokesTools'
```

### `ModNavigationTests` (Navigation Page + I18N Sample)

Verifies packaged `everywhere.sample.navigation-1.0.0.zip` navigation registration, English/Chinese resource switching, button interaction, and localization unregistration:

```powershell
$env:EVERYWHERE_UI_MOD_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net11.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_UI_MOD_PACKAGE = (Resolve-Path 'mods/samples/Everywhere.Mods.Sample/bin/Debug/net11.0/everywhere.sample.navigation-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter 'FullyQualifiedName~PackagedSample_ShowAndRemove_UpdatesTranslationsAndReportsCollection'
```

## 3. Known Limits

### Avalonia Cleanup Limits

- Avalonia registers `AvaloniaProperty` metadata, class handlers, routed events, and template caches in process-wide static tables that retain UI control types from a mod's `AssemblyLoadContext`.
- Because `ModEngine` checks `AssemblyLoadContext` collection via weak references after unloading, mods defining custom Avalonia controls currently produce `ModApplyStatus.RestartRequired` (`CollectionUnconfirmed`) even after navigation items and localization providers are detached.
- Host-wide `AvaloniaPropertyRegistry.UnregisterByModule` integration remains future work; the runtime safely handles `RestartRequired` by surfacing the diagnostic and keeping native libraries deferred until exit.
