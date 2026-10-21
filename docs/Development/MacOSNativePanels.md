# macOS native panel input

The selection toolbar uses a native NSPanel while retaining the existing Avalonia Window, presentation and pooled lifetime. Its constructor selects `AvaloniaNativeWindowFactory.CreatePanel(new() { IsNonActivating = true })` under `#if MACOS`. Windows and Linux keep their normal platform constructors. Creation happens before AXAML properties or opened events, so an initialized native window is never replaced.

## Package dependency

The compatibility fork is `Sylinko/Avalonia`, based on upstream 12.1.3 (`8eeda4f6f546165b3f72e63c9f42247abb306905`). Only `Avalonia.Native` is replaced; package and assembly versions stay **12.1.3**. This is an explicit exception to the feed's usual `999.YYYYMMDD.run` producer convention. The native and managed assets in this package form one unit. Do not substitute only the dylib or only the DLL.

The reviewed package is published through the Sylinko feed. The repository's `nuget.config` maps the exact `Avalonia.Native` ID to that feed; other Avalonia packages retain their existing sources. Normal restore and build commands use the published package without a local override:

```sh
dotnet restore src/Everywhere.Mac/Everywhere.Mac.csproj
dotnet build src/Everywhere.Mac/Everywhere.Mac.csproj -c Debug
```

NuGet reuses installed packages before consulting source mappings; see the [NuGet source mapping rules](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping#package-pattern-precedence). When switching from the official package with the same ID/version, close build processes and remove or back up only `avalonia.native/12.1.3` under the configured global packages folder before restoring. Do not clear unrelated packages. The restored package's `.nupkg.metadata` must name `https://nuget.sylinko.com/v3/index.json` as its source.

The published native library targets macOS 10.13 for Intel and 11.0 for Apple Silicon. Fork builds, publication and upstream upgrades are documented in the [Avalonia fork's maintenance guide](https://github.com/Sylinko/Avalonia/blob/sylinko/native-panels-12.1.3/docs/sylinko/native-panels.md).

## Input ownership

Preparation records the source application's PID and checks that the window is a non-activating NSPanel. It does not acquire keyboard input. Capture is UI-thread-owned, requires the same source, and allows one active lease per helper. It makes the existing AvnView first responder through AppKit's non-activating panel route. The regular Avalonia native text-input method remains installed. Native popups created by this panel, including nested menus, also use non-activating NSPanel instances. They stay non-key and share the parent view's input method. `TakesFocusFromNativeControl=false` alone cannot provide this: upstream popup hosts still call `Show(true)`, which explicitly activates the application when the parent is key. Ordinary windows and ChatWindow keep their existing popup path. Panel popups explicitly disable the native window shadow: NSPanel enables it by default, unlike the upstream borderless NSWindow. ToolTip and MenuFlyout already draw their own shadows inside the Avalonia surface; keeping both adds a dark outer rim.

`MacWindowInputCapture` observes native key loss and window lifetime. Explicit disposal unsubscribes first, resets Avalonia's input method to clear preedit, and resigns the panel's key status. It never activates a previous application or synthesizes corrective key events. Involuntary key loss raises Lost, and the toolbar hides through its existing owner. Switching back to tools releases the lease while retaining the same panel and draft.

During editing, macOS pauses global overlay observation and uses native key loss for outside-click dismissal. This keeps out-of-process IME candidate interaction out of geometry-based dismissal. Passive toolbar observation resumes when returning to tools. Scrolling outside without changing native key focus does not dismiss an editing macOS panel. This is an intentional platform policy; it avoids inventing ownership heuristics for third-party IME windows.

Debug logs record preparation, rejected capture conditions, completed native capture and logical editor focus. Their source PIDs and native type/style distinguish an unavailable route from a foreground change during acquisition; the existing toolbar warning identifies activated fallback separately.

Passive toolbar/effect windows do not count as ordinary Dock-visible application windows. Normal window activation and ChatWindow behavior retain their existing paths.

Source element focus can change after panel input is released, as observed with Edge's address bar. This remains an unresolved application-specific limitation; no source-focus restoration mechanism is included.

## Published package verification (2026-10-22)

Normal restore downloaded the published package from the Sylinko service index into the default global packages folder. Its archive matches SHA-256 `a75732e885f08288405f58b6a613f8f7932dd8c9440a9afd5f6ac84843142ffb` in the merged version manifest, from fork commit `f08f1de0c1510b00f1912b909a53f05daf6171fe`. Product assets no longer reference the local feed, isolated cache or fallback folders.

The macOS Debug build and development signing passed with eight existing Watchdog AOT/trim warnings and no errors. A standalone published-package probe passed ten factory/popup checks, and a separate probe using the freshly built product assemblies verified ToolTip/MenuFlyout with the actual selection toolbar and product styles. An unchanged normal build passed in 5.40 seconds with no warnings or errors and preserved all six monitored output timestamps and hashes, including native/managed assets and the signing stamp.

## Manual acceptance

- With the main/settings window behind a source app, show the passive toolbar; it must not acquire keyboard input or raise the main window.
- Enter freeform input, type English and Chinese, select candidates with keyboard and pointer, use editing/navigation keys and multiline input, and exercise Escape during and after composition.
- Open/close assistant menus and continue typing. Input must not activate the main/settings window.
- Return to tools, close, reopen, and repeat while animations are running. Check source input after release and unfinished-preedit cleanup.
- Click another application/window while editing; native key loss should hide the toolbar without restoring focus to an older source.
- Submit an accepted message and verify normal chat activation; test busy/rejected submission and editor reacquisition separately.
- Repeat with the source application full-screen, with Dock-visible application windows open, and with only the tray application running.

For subsequent releases, publish through the fork's manual workflow after testing and human review, merge the reviewed `Sylinko/nuget-feed` manifest PR, and confirm feed deployment before updating the consumer version.
