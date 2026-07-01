# How to build

## System Requirements

Building Everywhere requires a supported desktop operating system and the .NET SDK version used by the project.

| Platform | Minimum Version                                                            | Notes                                                   |
| -------- | -------------------------------------------------------------------------- | ------------------------------------------------------- |
| Windows  | Windows 10 version 19041 or later                                          | Matches the Windows runtime requirement for Everywhere. |
| macOS    | macOS Monterey 12.0 or later                                               | Matches the macOS runtime requirement for Everywhere.   |
| Linux    | Not specifically defined, but a X11-based desktop environment is required. | Linux support is in early stages of development.        |

## Core Components

| Component                | Recommended Version | Description                                                               | Related Links                                                                 |
| ------------------------ | ------------------- | ------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| Git                      | Latest release      | Supports Git LFS and submodules when cloning the repository.              | [Git Official Site](https://git-scm.com/)                                     |
| .NET SDK                 | 10.202              | Provides the core build and runtime environment for the project.          | [.NET Official Site](https://dotnet.microsoft.com/download)                   |
| JetBrains Rider          | >= 2026.1 Nightly   | Recommended cross-platform IDE for opening and building solution filters. | [Rider Official Site](https://www.jetbrains.com/rider/)                       |
| Xcode Command Line Tools | 26.3                | **Required** on macOS for native build toolchain support.                 | [Apple Developer Documentation](https://developer.apple.com/xcode/resources/) |

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

On macOS, install the Xcode Command Line Tools if they are not already available:

```bash
xcode-select --install
```

If above command is not working, you can also install Xcode from the App Store, or from [Apple Developer Documentation](https://developer.apple.com/xcode/resources/).

## Build

### 1. Clone the Repository

Everywhere uses Git submodules to manage some dependencies. Use the `--recursive` option when cloning the repository to ensure that all submodules are cloned correctly.

```bash
git clone https://github.com/Sylinko/Everywhere.git --recursive
```

If you have already cloned the repository but find missing folders or resource files, run the following commands from the repository root:

```bash
git submodule update --init --recursive
git lfs pull
```

### 2. Restore workloads and dependencies

```bash
dotnet workload restore Everywhere.slnx
dotnet restore Everywhere.slnx
```

> [!NOTE]
> If you are using an IDE, it may automatically restore workloads and dependencies when you open the solution. However, running the above commands ensures that all necessary components are in place before building.
> Especially we are using some local NuGet packages which are not published to any public feed, so `dotnet restore` is required to get those packages before building the project.

### 3. Build and Run

You can build the project using the command line or an IDE that supports .NET 10.

There are several platform-specific solution `slnx` files in the `src` directory which are intended to be built with CI/CD pipelines.
You can just open the main solution `Everywhere.slnx` in your IDE and build the project from there. Setting the target platform in the IDE will build the corresponding platform-specific project.
`Everywhere.Windows` is the Windows entry point, `Everywhere.Mac` is the macOS entry point, and `Everywhere.Linux` is the Linux entry point (not in active development yet).

If you want to build from the command line, you can specify the target project and configuration. For example, to build the Windows project in Debug configuration:

```bash
dotnet build src/Everywhere.Windows/Everywhere.Windows.csproj -c Debug
```

After the build completes, the output is located under the platform-specific project directory.

For Windows:

```text
src/Everywhere.Windows/bin/Debug/net10.0-windows10.0.19041.0/win-x64/
```

Run `Everywhere.exe` from that directory to start the application.

We recommend using the IDE to run the project, as it will automatically set the correct working directory and environment variables. It's also recommended to run the project with a single command. For example, to run the Windows project in Debug configuration:

```bash
dotnet run --project src/Everywhere.Windows/Everywhere.Windows.csproj -c Debug
```

## Frequently Asked Questions

### Why my Rider shows many errors in `axaml` files?

`axaml` files are Avalonia's XAML files, which are used for defining UI layouts. Older versions of Rider may not have proper support for Avalonia, leading to errors in `axaml` files. To resolve this issue, ensure that you are using the latest version of JetBrains Rider Nightly. After testing, 2026.1 Nightly or later versions should have improved support for Avalonia and should not show errors in `axaml` files.

### Why sometimes I get build errors related to file are occupied or locked by another process?

This can happen if the application is still running or if there are background building processes (e.g. dotnet.exe) that have not completed. Make sure to close any running instances of Everywhere before building again. If it's locked by dotnet.exe, you can run the following command to kill the process:

```bash
dotnet build-server shutdown
```
