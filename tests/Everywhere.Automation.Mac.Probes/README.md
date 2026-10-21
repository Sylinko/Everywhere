# macOS Automation Probes

This project is an explicit native macOS probe runner. It is an executable `Microsoft.macOS` app bundle, not a VSTest test assembly. That distinction is required because `ObjCRuntime` resolves `xamarin_initialize` from the `__Internal` symbols exported by the bundle's Mach-O app host.

Run it from the repository root:

```shell
dotnet build tests/Everywhere.Automation.Mac.Probes/Everywhere.Automation.Mac.Probes.csproj -t:RunProbes
```

The custom target launches the executable inside the generated app bundle so that stdout and the exit code return to the terminal. The runner writes one JSON report and exits with code `0` only when every required invariant passes. Do not run the managed DLL through `dotnet` or VSTest; that replaces the native host and reproduces the missing `__Internal/xamarin_initialize` failure. Plain `dotnet run` is also unsuitable for these probes because the macOS SDK's app launch path does not reliably forward bundle stdout or its exit code.

The initial probes cover:

- native app-host and `ObjCRuntime` initialization;
- distinct AX pointers that compare equal through Core Foundation identity;
- positional and stop-on-error `AXUIElementCopyMultipleAttributeValues` result shapes;
- raw Core Foundation scalar decoding without disposable aliases for borrowed batch slots;
- successful production batch decoding of a focused AppKit text field's role, states, value, and bounds;
- one-timeout production failure behavior when that same provider is blocked;
- process-global versus exact-reference `AXUIElementSetMessagingTimeout` behavior against a controlled AppKit provider;
- concurrent progress against a responsive provider while another AppKit provider is blocked;
- Context-local Identity Map canonicalization and backend-owned IDs;
- retention release and later-incarnation behavior;
- isolation between different `VisualContext` instances;
- `Default`, `NativeWindow`, `TopLevel`, and `Screen` query semantics against a controlled native window;
- the projected `Screen -> AXWindow -> AX descendants` graph, including canonical identity reuse and isolated `AXApplication`/`AXSystemWide` roots;
- exclusion of compositor-owned Quartz surfaces through the live `NSRunningApplication` boundary before any `AXWindows` provider call;
- notification-driven immutable display-topology replacement, generation advancement, and stale Screen invalidation;
- multiple AppKit windows and an `NSPanel` following Quartz Z-order, plus hidden, minimized, closed, and sheet lifetime/topology behavior;
- production `ReadText` paging through `AXNumberOfCharacters` and `AXStringForRange` against a 100,000-character `NSTextView`, including complete 257-character-page reconstruction, nonzero offsets, the final page, exhaustion, and a one-character limit at a UTF-16 surrogate pair;
- `AXValue` fallback paging on a controlled element that rejects `AXNumberOfCharacters`, plus a blocked-provider read that returns after one messaging timeout without attempting the fallback;
- indexed 64-element `AXChildren` pages against a 130-child group, including operation-local page retention across provider mutation.

Pass `--capture` directly to the bundle executable for the focused pixel-contract probe. It creates a four-quadrant AppKit window and validates RGBA byte order, top-left row orientation, AX descendant cropping, ordinary shadowed/minimized/borderless FullSize mapping, complete surfaces for fully occluded and partially off-desktop windows, and Screen capture. It also characterizes the macOS 15.7.3 boundary where a window with zero display intersection remains directly reacquirable but `CGSHWCaptureWindowList` returns no image. The fixture overrides AppKit's normal keep-visible frame constraint only to construct that state; production never moves a target window for capture. The report records `CGPreflightScreenCaptureAccess`; grant Screen Recording permission to the probe app and restart the host before treating a Screen result as evidence. On 2026-09-10 the complete capture contract passed while Stage Manager was globally enabled, validating the active Stage; inactive Stages and separate Spaces remain uncharacterized. Rotated displays and mixed-density display arrangements remain separate hardware-dependent cases.

Pass `--query-semantics` to run only the focused root/relation contract. It additionally verifies that a non-window AX descendant reports `NativeWindowHandle` as unavailable without a provider failure or inheritance from its enclosing `AXWindow`.

The timeout probe starts the same native executable in a provider mode. That child creates an AppKit `NSWindow`, `NSTextField`, and `NSButton`, runs the normal `NSApplication` event loop, and accepts a small stdin protocol that can block and resume its main thread. The parent remains the AX client and owns cleanup of that exact child process.

Future multi-display, permission-transition, full-screen Space, and additional third-party Value-only compatibility probes can be added here without introducing test-only hooks into the production backend.

## Panel discovery conclusion

On 2026-09-07 (macOS 15.7.3, Arm64, .NET 10.0.6), a focused investigation found that an inactive provider's `NSPanel` with `HidesOnDeactivate=true` could report `IsVisible=true` and `AccessibilityElement=true` while both `AXWindows` and the exact-ID Quartz window list omitted it. `MakeKeyAndOrderFront` alone did not restore discovery. Changing `HidesOnDeactivate` to false at the next step restored Quartz, AXWindows, and existing backend resolution; no accessibility override or floating-panel change was needed.

This identifies an activation-sensitive fixture precondition rather than an AXWindows-only production discovery gap. The transitions were cumulative, so they do not establish independent state behavior or explain every earlier failure. The one-time diagnostic has been retired; the native host's window-state and identity acceptance probes remain.
