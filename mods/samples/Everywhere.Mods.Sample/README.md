# Navigation mod sample

This sample uses compiled AXAML, generated localization keys, neutral RESX resources, and a standard .NET satellite assembly. It adds **Mod sample / 模组示例** to the main navigation. Change Everywhere's language in Settings and try the button on the page.

## Build and try

1. From the repository root, pack the mod directly (the project builds `Everywhere.Modding.Sdk` and `Everywhere.Core` automatically):

   ```powershell
   dotnet build mods/samples/Everywhere.Mods.Sample/Everywhere.Mods.Sample.csproj -c Debug -t:EverywherePack
   ```

2. Import the generated `mods/samples/Everywhere.Mods.Sample/bin/Debug/net11.0/everywhere.sample.navigation-1.0.0.zip` or unpack directory through `IModManager` (`ImportZipAsync` / `ImportDirectoryAsync`) or copy `Everywhere.local.props.example` to `Everywhere.local.props` to target an installed host directory instead of `Everywhere.Core.csproj`.

To run the headless integration test after building the host and sample package:

```powershell
$env:EVERYWHERE_UI_MOD_HOST = (Resolve-Path 'src/Everywhere.Windows/bin/Debug/net11.0-windows10.0.19041.0/win-x64').Path
$env:EVERYWHERE_UI_MOD_PACKAGE = (Resolve-Path 'mods/samples/Everywhere.Mods.Sample/bin/Debug/net11.0/everywhere.sample.navigation-1.0.0.zip').Path
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj --filter 'FullyQualifiedName=Everywhere.Core.Tests.Modding.ModNavigationTests.PackagedSample_ShowAndRemove_UpdatesTranslationsAndReportsCollection'
```
