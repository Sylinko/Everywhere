# Everything search mod

[简体中文](README.zh-Hans.md)

Search local files and folders through Everything from Everywhere's chat tools.
This mod is built into the Windows x64 distribution via `EmbeddedModSource` (`builtin`) and targets **Windows x64**. Other platforms report `Skipped` / `UnsupportedPlatform`.

## How it connects to Everything

The mod connects to or launches Everything on demand at the first search request in this order:

1. Running default instance, including Everything 1.4.
2. Running Everything 1.5 `1.5a` instance.
3. Existing user installation found via configured `ExecutablePath`, App Paths, or standard install locations.
4. Bundled private Everything 1.5 instance stored under `~/.everywhere/mods-data/everywhere.everything` when no user installation is available.

User-owned Everything processes remain untouched when the mod stops or unloads. Only the private fallback process is supervised by Watchdog and shut down on mod disposal.

The bundled fallback requires either an already-running compatible Everything 1.5 indexing service or an elevated host process. The mod never installs services or requests UAC elevation implicitly.

## Settings

Open the Chat Plugins page (or Mods settings) and configure either field if needed:

- **Everything executable**: Optional absolute path to `Everything.exe` when starting a portable installation whose requested instance is not running.
- **Everything instance name**: Optional exact IPC instance name (`1.5a` or custom). Leave empty for automatic discovery.

Changes persist automatically under `~/.everywhere/mods-data/everywhere.everything/settings.json` and take effect on the next search query.

## Build from source

From the repository root:

```powershell
dotnet build src/Everywhere.Modding.Sdk/Everywhere.Modding.Sdk.csproj -c Debug
dotnet build mods/Everywhere.Mods.Everything/Everywhere.Mods.Everything.csproj -c Debug -t:EverywherePack
```

By default, the project references `src/Everywhere.Core/Everywhere.Core.csproj` with host compatibility range `[0.9.0-0,)`. Copy `Everywhere.local.props.example` to `Everywhere.local.props` to point at an installed host directory instead.

Bundled executables come from [voidtools](https://www.voidtools.com/downloads/). Third-party notices are included under `Licenses/`.
