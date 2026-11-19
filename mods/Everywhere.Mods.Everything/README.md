# Everything search mod

[简体中文](README.zh-Hans.md)

Search local files and folders through Everything from Everywhere's chat tools.
This mod supports **Windows x64**. Other platforms report `Skipped` / `UnsupportedPlatform`.

## Install

With Everywhere stopped, put `everywhere.everything-1.0.0.zip` in `~/.everywhere/mods`
and start the mod-enabled application. Keep the ZIP there; startup imports an immutable
runtime copy. Replace or remove the source ZIP while the application is stopped. Mod data
is retained under `~/.everywhere/mods-data/everywhere.everything`.

## Choose an Everything instance

No process starts until the first query. Automatic selection tries:

1. The running default Everything instance, including Everything 1.4.
2. The running Everything 1.5 `1.5a` instance.
3. An installed Everything application, started normally if necessary.
4. The bundled Everything 1.5.0.1424b, in a private instance, when no user installation exists.

User-owned processes remain running when the mod stops. Only the private bundled instance
is registered with Watchdog and stopped by the mod. Its configuration and database stay in
the mod data directory.

The bundled instance needs a compatible running Everything 1.5 indexing service or existing
administrator privileges. An Everything 1.4 service is not compatible. The mod does not
install or upgrade services, request elevation, or change the user's Everything installation.

## Settings

On the chat tools page, select Everything and open Settings. Both fields are optional:

- **Executable path:** the user installation to start when the selected endpoint is unavailable.
  Set this for portable installations outside the normal installation locations.
- **Instance name:** a particular Everything 1.5 index. An explicit name never silently falls
  back to another index. Leave both fields empty for automatic selection.

Edits are saved automatically and take effect on the next query, including calls from existing
chats. They do not interrupt an active query or stop a user-owned process. Finish editing before
starting a query. Settings live in `~/.everywhere/mods-data/everywhere.everything/settings.json`;
edit that file directly only while Everywhere is stopped.

Everything 1.4 uses SDK 2, whose synchronous queries cannot be aborted. Unloading must wait for
an active query; an unresponsive query can require a restart. Everything 1.5 uses SDK 3 when its
endpoint is available, supporting cancellation and named instances.

## Build from source

From the Everywhere repository root:

```powershell
dotnet build src/Everywhere.Modding.Sdk/Everywhere.Modding.Sdk.csproj -c Debug
dotnet build mods/Everywhere.Mods.Everything/Everywhere.Mods.Everything.csproj -c Debug -t:EverywherePack
```

The default host is the Windows source project, built through ProjectReference with its normal
prerequisites, including Watchdog publishing. For another host input, copy
`Everywhere.local.props.example` to the ignored `Everywhere.local.props` file and set the path.
The declared host range must include that host; the project currently uses `[1.0.0]`.

The output is `bin/Debug/net10.0-windows10.0.19041.0/win-x64/everywhere.everything-1.0.0.zip`.
The SDK includes EverythingNetCore and the x64 native runtime, excluding host-owned assemblies.
See the [developer guide](../../docs/Modding/02-ModDeveloperGuide.md) for host imports,
compatibility and packaging, and the [validation record](../../docs/Modding/03-Validation.md)
for integration tests and remaining desktop acceptance checks.

## Run the packaged integration test

After building the package, select the explicit test by its exact name:

```powershell
$env:EVERYWHERE_EVERYTHING_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net10.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_EVERYTHING_PACKAGE = (Resolve-Path 'mods/Everywhere.Mods.Everything/bin/Debug/net10.0-windows10.0.19041.0/win-x64/everywhere.everything-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter 'FullyQualifiedName=Everywhere.Core.Tests.Modding.EverythingModTests.PackagedEverything_QueryAndUnload_PreservesUserProcessAndRevokesTools'
```

The test uses two isolated temporary Everything instances. It checks the embedded icon, English/Chinese
settings, query-time setting changes, final saving, retained-call revocation and managed/native unload.
It does not install services or change the user's existing Everything instances.

The bundled runtime comes from [voidtools](https://www.voidtools.com/downloads/).
EverythingNetCore, Everything and SDK license notices are included under `Licenses`.
