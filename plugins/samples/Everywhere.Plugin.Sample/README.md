# Navigation plugin sample

This sample uses compiled AXAML, generated localization keys, neutral RESX resources and a standard .NET satellite assembly. It adds **Plugin sample / 插件示例** to the main navigation. Change Everywhere's language in Settings and try the button on the page.

## Build and try

1. Build the plugin-enabled Windows host with `dotnet build src/Everywhere.Windows/Everywhere.Windows.csproj`. Build the SDK with `dotnet build src/Everywhere.Plugin.Sdk/Everywhere.Plugin.Sdk.csproj`.
2. Copy `Everywhere.local.props.example` to `Everywhere.local.props` in this directory. Set `EverywhereHost` to the host output directory (or its source `.csproj`) and choose `EverywhereHostVersion` using the exported `plugin-host.json`. The local file is ignored by Git. The sample imports the workspace SDK until a NuGet SDK is released.
3. Run `dotnet msbuild plugins/samples/Everywhere.Plugin.Sample/Everywhere.Plugin.Sample.csproj -restore -target:EverywherePack` from the repository root.
4. Put the generated `bin/Debug/net10.0/everywhere.sample.navigation-1.0.0.zip` directly in `~/.everywhere/plugins/`. Alternatively, put an unpacked package folder there, with `plugin.json` directly inside it. Keep only one source per plugin; do not leave both the ZIP and extracted folder.
5. Run the newly built Everywhere. The currently installed older application will not gain this startup loader automatically.

The host reads original ZIPs and immediate child package folders once at startup, validates the selected set, and records outcomes in its normal log. It copies/extracts them to `%APPDATA%/Everywhere/plugin-installations/packages/` and reuses unchanged imports on later launches. Temporary imports live in `%APPDATA%/Everywhere/cache/plugin-staging/`. The source directory is the user-facing management location; do not edit generated installation copies. Source changes or removal take effect at the next launch. Persistent plugin data belongs in `~/.everywhere/plugin-data/{id}/` and is not removed with code. A management page, persisted disablement and automatic removal of superseded installation copies remain pending.

The sample directly references Avalonia for its XAML build tasks. The SDK imports the host's declared assemblies and excludes host-owned DLLs from the ZIP. Its build-time I18N generator comes from the repository; distributing that generator through the plugin SDK remains separate work.

## Initial validation and limits

The headless integration check renders the packaged page, switches English/Chinese, invokes the button, removes the selected navigation contribution and checks that localization is unregistered. The current production cleanup path still returns `RestartRequired`. An isolated follow-up identified Avalonia property-metadata caches retaining `SamplePage` and confirmed assembly/context collection after the public `UnregisterByModule` API was called. A general solution is still under investigation: assembly-owned PluginResource registration is implemented, but Avalonia does not yet automatically register its cleanup. Authors are not required to copy the diagnostic cleanup into every plugin. This sample does **not** establish hot-unload support for all custom controls. See [cleanup evidence and limits](../../../docs/PluginEngine/03-Validation.md#avalonia-cleanup-evidence-and-limits). Visual behavior in the real desktop application still needs manual validation.

To reproduce the explicit integration check after building the host and package:

```powershell
$env:EVERYWHERE_UI_PLUGIN_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net10.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_UI_PLUGIN_PACKAGE = (Resolve-Path 'plugins/samples/Everywhere.Plugin.Sample/bin/Debug/net10.0/everywhere.sample.navigation-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter 'FullyQualifiedName=Everywhere.Core.Tests.Plugins.PluginNavigationTests.PackagedSample_ShowAndRemove_UpdatesTranslationsAndReportsCollection'
```

The test process uses its own built copies of the host's declared shared dependencies. It checks the real runtime and UI contribution APIs, without starting the application bootstrap or platform helper processes.