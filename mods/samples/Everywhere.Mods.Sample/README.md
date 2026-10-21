# Navigation mod sample

This sample uses compiled AXAML, generated localization keys, neutral RESX resources and a standard .NET satellite assembly. It adds **Mod sample / 模组示例** to the main navigation. Change Everywhere's language in Settings and try the button on the page.

## Build and try

1. Build the mod-enabled Windows host with `dotnet build src/Everywhere.Windows/Everywhere.Windows.csproj`. Build the SDK with `dotnet build src/Everywhere.Modding.Sdk/Everywhere.Modding.Sdk.csproj`.
2. Copy `Everywhere.local.props.example` to `Everywhere.local.props` in this directory. Set `EverywhereHost` to the host output directory and choose `EverywhereHostVersion` using the exported `mod-host.json`. The local file is ignored by Git. The sample imports the workspace SDK until a NuGet SDK is released. Source `.csproj` inputs now use ProjectReference and require a compatible target framework: referencing the Windows entry project requires a Windows-targeted sample build, not the current neutral `net10.0` configuration. Use a separate platform entry project when distributing platform-specific implementations.
3. Run `dotnet msbuild mods/samples/Everywhere.Mods.Sample/Everywhere.Mods.Sample.csproj -restore -target:EverywherePack` from the repository root.
4. Put the generated `bin/Debug/net10.0/everywhere.sample.navigation-1.0.0.zip` directly in `~/.everywhere/mods/`. Alternatively, put an unpacked package folder there, with `mod.json` directly inside it. Keep only one source per mod; do not leave both the ZIP and extracted folder.
5. Run the newly built Everywhere. The currently installed older application will not gain this startup loader automatically.

The host reads original ZIPs and immediate child package folders once at startup, validates the selected set, and records outcomes in its normal log. It copies/extracts them to `%APPDATA%/Everywhere/cache/mods/packages/` and reuses unchanged imports on later launches. Temporary imports live in `%APPDATA%/Everywhere/cache/mods/staging/`. The source directory is the user-facing management location; do not edit generated installation copies. After exiting Everywhere, you may delete the entire `cache/mods` directory; the next startup rebuilds it from the retained sources without deleting mod data. Source changes or removal take effect at the next launch. Persistent mod data belongs in `~/.everywhere/mods-data/{id}/` and is not removed with code. A management page, persisted disablement and automatic removal of superseded installation copies remain pending.

The sample directly references Avalonia for its XAML build tasks. The SDK imports the host's declared assemblies and excludes host-owned DLLs from the ZIP. Its build-time I18N generator comes from the repository; distributing that generator through the mod SDK remains separate work.

## Initial validation and limits

The headless integration check renders the packaged page, switches English/Chinese, invokes the button, removes the selected navigation contribution and checks that localization is unregistered. The current production cleanup path still returns `RestartRequired`. An isolated follow-up identified Avalonia property-metadata caches retaining `SamplePage` and confirmed assembly/context collection after the public `UnregisterByModule` API was called. A general solution is still under investigation: assembly-owned ModResource registration is implemented, but Avalonia does not yet automatically register its cleanup. Authors are not required to copy the diagnostic cleanup into every mod. This sample does **not** establish hot-unload support for all custom controls. See [cleanup evidence and limits](../../../docs/Modding/03-Validation.md#avalonia-cleanup-evidence-and-limits). Visual behavior in the real desktop application still needs manual validation.

To reproduce the explicit integration check after building the host and package:

```powershell
$env:EVERYWHERE_UI_MOD_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net10.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_UI_MOD_PACKAGE = (Resolve-Path 'mods/samples/Everywhere.Mods.Sample/bin/Debug/net10.0/everywhere.sample.navigation-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter 'FullyQualifiedName=Everywhere.Core.Tests.Modding.ModNavigationTests.PackagedSample_ShowAndRemove_UpdatesTranslationsAndReportsCollection'
```

The test process uses its own built copies of the host's declared shared dependencies. It checks the real runtime and UI contribution APIs, without starting the application bootstrap or platform helper processes.
