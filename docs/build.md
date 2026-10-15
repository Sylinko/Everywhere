# How to build

For contribution guidelines and links to architecture, development, and testing documentation, start with the
[contributor guide](../.github/CONTRIBUTING.md#documentation). Browse the [docs directory](.) for the current topic documents;
this guide focuses on setting up the toolchain and building the application.

## System Requirements

The following are application runtime requirements for the current release toolchain. The development machine must
also meet the requirements of the .NET SDK and, on macOS, the selected Xcode version, which can require a newer
operating system.

| Platform | Minimum Version                                                            | Notes                                                   |
|----------|----------------------------------------------------------------------------|---------------------------------------------------------|
| Windows  | Windows 10 version 19041 or later                                          | Matches the Windows runtime requirement for Everywhere. |
| macOS    | macOS Monterey 12.0 or later                                              | Depends on the build toolchain; see below.              |
| Linux    | Not specifically defined, but a X11-based desktop environment is required. | Linux support is in early stages of development.        |

## Core Components

| Component                | Recommended Version | Description                                                               | Related Links                                                                 |
|--------------------------|---------------------|---------------------------------------------------------------------------|-------------------------------------------------------------------------------|
| Git                      | Latest release      | Supports Git LFS and submodules when cloning the repository.              | [Git Official Site](https://git-scm.com/)                                     |
| .NET SDK                 | See [global.json](../global.json) | Provides the core build and runtime environment for the project. | [.NET Official Site](https://dotnet.microsoft.com/download)                   |
| JetBrains Rider          | >= 2026.1 Nightly   | Recommended cross-platform IDE for opening and building solution filters. | [Rider Official Site](https://www.jetbrains.com/rider/)                       |
| Xcode                    | Match the macOS workload | **Required** on macOS; install the full Xcode application.           | [Apple Developer Documentation](https://developer.apple.com/xcode/resources/) |

> Ensure that your IDE supports .NET 10 before opening the solution.

## Configuration

Before building the project, initialize Git LFS:

```bash
git lfs install
```

On Windows, enable long path support. Run the following command as an administrator:

```bash
git config --global core.longpaths true
```

On macOS, install the full Xcode application from the App Store or
[Apple Developer Downloads](https://developer.apple.com/download/all/). Standalone Command Line Tools installed with
`xcode-select --install` are not sufficient for the .NET macOS app build. Open Xcode once to complete its setup, then
check which installation is selected:

```bash
xcode-select -p
xcodebuild -version
```

If necessary, select the installed Xcode application (adjust the path for a versioned application name):

```bash
sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer
```

### macOS workload and Xcode compatibility

[global.json](../global.json) selects the .NET SDK but intentionally does not pin `sdk.workloadVersion` for all
contributors. Choose a macOS workload that supports your installed Xcode. A newer Xcode is not automatically compatible
with an older workload, and changing the application's minimum macOS version does not resolve a toolchain mismatch.

For .NET SDK 10.0.401, the following workload sets provide these combinations:

| Xcode | Workload set | .NET macOS workload | Minimum macOS for the built app |
|-------|--------------|---------------------|---------------------------------|
| 26.6  | 10.0.401     | 26.5.10318           | 12.0                            |
| 27.0  | 10.0.401.1   | 27.0.10722           | 14.0                            |

These are versioned examples, not a requirement to keep every developer on the same Xcode. For other SDK or Xcode
versions, use the installation instructions in the [.NET Apple SDK releases](https://github.com/dotnet/macios/releases)
and check [Apple's host system requirements](https://developer.apple.com/xcode/system-requirements).

From the repository root, confirm the selected SDK and install the matching workload set. For Xcode 27.0 with
.NET SDK 10.0.401:

```bash
dotnet --version
sudo dotnet workload install macos --version 10.0.401.1
dotnet workload --version
dotnet workload list
```

For Xcode 26.6, use `--version 10.0.401` instead. `--version` selects a workload set, not the .NET SDK version or the
individual macOS workload version. It records the selection locally for subsequent workload installs/restores in that
SDK feature band. This can affect other projects using the same .NET installation and feature band, but does not change
other contributors' environments. See [.NET workload sets](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-sets).

Avoid running an unversioned `dotnet workload update` when you need to retain that pairing: it can select a newer set.
The [macOS release workflow](../.github/workflows/macos-release.yml) pairs Xcode 26.6 with workload set 10.0.401.
Update these together when changing the release toolchain, rather than adding a shared workload pin to `global.json`.

### Minimum macOS version of a build

Local and CI builds use the same deployment-target policy: `SupportedOSPlatformVersion` defaults to the selected
Apple SDK's `MinSupportedOSPlatformVersion`. This is set in
[the macOS project's Directory.Build.targets](../src/Everywhere.Mac/Directory.Build.targets), after the SDK has loaded
its minimum version. Simply omitting the property would instead default to the target platform version, which can
unnecessarily raise the app's minimum runtime requirement.

The source `Info.plist` intentionally omits `LSMinimumSystemVersion`. The SDK writes that key into the generated app
manifest from `SupportedOSPlatformVersion`, so developers do not need to edit the project or plist when switching
between supported workloads. An explicitly supplied `SupportedOSPlatformVersion` is still respected and must satisfy
the selected SDK's requirements.

CI does not separately pin the minimum macOS version. Its current toolchain produces apps targeting macOS 12.0;
the Xcode 27.0 workload produces apps targeting macOS 14.0. Upgrading the CI toolchain can therefore raise the minimum
version of published apps. Review the resulting deployment target and update the public system requirements when
making that change. Builds made with a newer workload do not establish compatibility with older macOS releases.

To inspect the evaluated versions without building:

```bash
dotnet msbuild src/Everywhere.Mac/Everywhere.Mac.csproj -getProperty:MinSupportedOSPlatformVersion,SupportedOSPlatformVersion
```

If a build reports that the workload requires a different Xcode version, check the selected Xcode and workload first.
Disabling `ValidateXcodeVersion` only bypasses validation; it does not make the toolchains compatible.

## Build

### 1. Clone the Repository

Everywhere uses Git submodules to manage some dependencies. Use the `--recursive` option when cloning the repository to
ensure that all submodules are cloned correctly.

```bash
git clone https://github.com/Sylinko/Everywhere.git --recursive
```

If you have already cloned the repository but find missing folders or resource files, run the following commands from
the repository root:

```bash
git submodule update --init --recursive
git lfs pull
```

### 2. Restore workloads and dependencies

Use the solution for your platform. Windows and Linux application builds do not require the `macos` workload or an
additional .NET workload installation. Windows native build tools are separate from .NET workloads.

On Windows:

```bash
dotnet restore Everywhere.Windows.slnx
```

On macOS, install the matching workload as described above, then restore:

```bash
dotnet restore Everywhere.Mac.slnx
```

On Linux:

```bash
dotnet restore Everywhere.Linux.slnx
```

> [!NOTE]
> An IDE may restore dependencies automatically. Run the appropriate command above from the repository root to use
> the package sources and mappings in [nuget.config](../nuget.config). For forked packages, custom feeds, and local
> package development, see [dependency packages](Development/DependencyPackages.md).

### 3. Build and Run

You can build the project using the command line or an IDE that supports .NET 10.

The platform-specific solutions `Everywhere.Windows.slnx`, `Everywhere.Mac.slnx`, and `Everywhere.Linux.slnx` are in the
repository root. Open the solution for your platform in the IDE to avoid restoring or building unrelated platform
projects. The main `Everywhere.slnx` includes projects and tests for multiple platforms and is useful for browsing the
whole repository, but building it can require toolchains for other platforms.
`Everywhere.Windows` is the Windows entry point, `Everywhere.Mac` is the macOS entry point, and `Everywhere.Linux` is
the Linux entry point (not in active development yet).

If you want to build from the command line, you can specify the target project and configuration. For example, to build
the Windows project in Debug configuration:

```bash
dotnet build src/Everywhere.Windows/Everywhere.Windows.csproj -c Debug
```

After the build completes, the output is located under the platform-specific project directory.

For Windows:

```text
src/Everywhere.Windows/bin/Debug/net10.0-windows10.0.19041.0/win-x64/
```

Run `Everywhere.exe` from that directory to start the application.

We recommend using the IDE to run the project, as it will automatically set the correct working directory and
environment variables. It's also recommended to run the project with a single command. For example, to run the Windows
project in Debug configuration:

```bash
dotnet run --project src/Everywhere.Windows/Everywhere.Windows.csproj -c Debug
```

### macOS Debug signing

The first Debug build of `Everywhere.Mac` creates a self-signed development identity in the repository-local
`.macos-signing` directory. The directory contains a dedicated keychain and its random password, is ignored by Git, and
is not added permanently to the user's keychain search list. Subsequent builds reuse the same identity without importing
it into the login keychain or repeatedly asking for keychain access.

This identity is only for running the Debug app locally. Publish builds retain the existing distribution boundary:
hardened runtime, entitlements, timestamps, and externally supplied Apple signing identities are handled by the Publish
workflow. The repository-local identity is not available to that workflow.

To replace a broken or expired local identity, move `.macos-signing` out of the repository and build again. The next
Debug build creates a new identity.

### Adding files to the macOS app bundle

Files copied into `Everywhere.app` after `_CreateAppBundle` must participate in both incremental copying and Debug
signing. Use this target shape as a template:

```xml

<Target Name="CopyMyHelperToMacAppBundle"
        AfterTargets="_CreateAppBundle"
        Inputs="$(MyHelperSource)"
        Outputs="$(IntermediateOutputPath)my-helper-bundle-copy.stamp;$(MyHelperBundlePath)"
        Condition="'$(IsMacEnabled)' == 'true'">
  <MakeDir Directories="$([System.IO.Path]::GetDirectoryName('$(MyHelperBundlePath)'))"/>
  <Copy SourceFiles="$(MyHelperSource)" DestinationFiles="$(MyHelperBundlePath)"/>
  <Exec Command="chmod +x &quot;$(MyHelperBundlePath)&quot;"/>
  <Touch Files="$(MyHelperBundlePath)"/>
  <Touch Files="$(IntermediateOutputPath)my-helper-bundle-copy.stamp" AlwaysCreate="true"/>
</Target>
```

Also add the copy stamp to `_DevSigningInput` in `Everywhere.Mac.csproj`. The stamp ensures that signing runs even when
`Copy` preserves an old source timestamp. `Touch` keeps every declared output newer than every input, which is
especially important when one target copies several files with different source timestamps.

Keep these invariants when adapting the template:

- Declare every source in `Inputs` and the stamp plus every bundled destination in `Outputs`.
- Do not use `SkipUnchangedFiles` against a signed destination. A signature changes the destination bytes, so it no
  longer matches the unsigned source.
- Touch copied destinations before touching the copy stamp.
- Make the copy stamp a Debug-signing input. The Debug signer automatically signs Mach-O libraries and executable files
  before the outer app bundle; keep executable permissions on native helpers so they are discovered.
- Add distributable helpers to the nested-code section of `_CoreSignAppBundle` as well. Publish keeps its externally
  supplied identity, hardened-runtime, entitlement, and timestamp boundary.
- Use `WriteOnlyWhenDifferent="true"` for generated source files so unchanged generated content does not invalidate
  downstream compilation and app-bundle creation.

## Frequently Asked Questions

### Why my Rider shows many errors in `axaml` files?

`axaml` files are Avalonia's XAML files, which are used for defining UI layouts. Older versions of Rider may not have
proper support for Avalonia, leading to errors in `axaml` files. To resolve this issue, ensure that you are using the
latest version of JetBrains Rider Nightly. After testing, 2026.1 Nightly or later versions should have improved support
for Avalonia and should not show errors in `axaml` files.

### Why sometimes I get build errors related to file are occupied or locked by another process?

This can happen if the application is still running or if there are background building processes (e.g. dotnet.exe) that
have not completed. Make sure to close any running instances of Everywhere before building again. If it's locked by
dotnet.exe, you can run the following command to kill the process:

```bash
dotnet build-server shutdown
```
