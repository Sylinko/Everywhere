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

`WebViewAxDiagnostics.swift` is a temporary raw AX diagnostic for a running Native WebView TestApp. Compile it with `xcrun swiftc ... -framework ApplicationServices -framework Foundation`, then pass the host PID, decimal Quartz window number, and maximum depth. `--summary` records the cross-process PID/role/AXWindow chain without dumping every scalar value.

The timeout probe starts the same native executable in a provider mode. That child creates an AppKit `NSWindow`, `NSTextField`, and `NSButton`, runs the normal `NSApplication` event loop, and accepts a small stdin protocol that can block and resume its main thread. The parent remains the AX client and owns cleanup of that exact child process.

Future multi-display, permission-transition, full-screen Space, and additional third-party Value-only compatibility probes can be added here without introducing test-only hooks into the production backend.

## Panel discovery diagnostic

Pass `--panel-discovery` directly to the bundle's `Contents/MacOS/Everywhere.Automation.Mac.Probes` executable to run the focused diagnostic instead of the full suite. It records provider-side AppKit state, raw AXWindows/AXChildren, focus and hit-test references, CFEqual comparisons, exact-ID Quartz information, and production NativeWindow resolution. State changes are cumulative, not independent experiments. The provider acknowledges each command on its main thread; this does not guarantee that application activation or Window Server state has settled. A successful diagnostic exit means observation completed, not that every state must resolve a window.

On 2026-09-07 (macOS 15.7.3, Arm64, .NET 10.0.6):

- One full-suite run passed all 12 probes without changing production window discovery, including panel, hidden/minimized windows, sheet ownership, and close behavior. This does not establish repeatability.
- A subsequent focused run reproduced the missing panel. The provider reported `HidesOnDeactivate=true`, `Active=false`, `IsVisible=true`, `AccessibilityElement=true`, and `AXWindow/AXDialog`. AXWindows omitted the panel, and `CGWindowListCopyWindowInfo(IncludingWindow, panelId)` returned an empty array. Provider-side `IsVisible` alone was therefore insufficient evidence of a discoverable Window Server window.
- Calling `MakeKeyAndOrderFront` did not establish activation in that run and did not restore discovery. Changing only `HidesOnDeactivate` to false at the next step restored the exact Quartz entry, AXWindows entry, and existing backend resolution. No AccessibilityElement override or floating-panel change was needed for this transition.
- Later setting `FloatingPanel=true` produced Quartz layer 3 while preserving discovery. Focus and hit-test references sometimes compared CFEqual; the hit was the AXWindow itself, whose AXWindow attribute was absent. This is not evidence about descendant-to-window traversal.

These observations identify an activation-sensitive fixture precondition, not an AXWindows-only production discovery gap. They do not prove every earlier failure had the same cause. Pending design confirmation: use a persistent panel for deterministic Z-order tests and retain a separate default auto-hiding-panel diagnostic. No production fallback or default fixture change has been applied; extraction into a standalone AppKit TestApp is paused at this decision.
