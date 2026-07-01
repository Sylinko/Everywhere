# Scoped UI Automation Element Picking: From Hacks to Reverse Engineering

## Chapter 4: The Overlay Occlusion Problem — How a Fullscreen Window Breaks Chromium's UIA

### The New Architecture, Still Not Quite Right

After all the work in the previous chapters — reverse-engineering `CUIAutomation::ElementFromPointHelper`, building the telemetry-string scanner, rewriting `ScreenSelectionSession` to drop every hook — we had a clean, working implementation. The overlay window was a normal Avalonia window receiving standard pointer events. `UIAutomationHelper.ElementFromPointer` used `UiaNodeFromHandle` to scope the UIA query to the target application's root window.

It worked. Mostly.

Some windows only returned a window-level element. No matter where the cursor moved, the result was always the root — never a button, a list item, a text block. The exact same coordinates passed to the original global `IUIAutomation::ElementFromPoint` returned the correct deep element without issue. The scoped version could not get in.

---

### The Investigation: A 1/4-Screen Test Rig

To isolate the problem, a simple experiment: resize the mask overlay to cover only the **top-left quarter** of the screen, but in the `PickElement` handler, multiply the cursor coordinates by 2 before passing them to `ElementFromPointer`. The bottom-right quadrant of the screen is now a test area completely unobscured by the overlay, yet the picking logic still exercises the full coordinate space.

The test target: an Electron application.

With the test application in the unobscured bottom-right quadrant, `ElementFromPointer` worked perfectly. Every button, every list item, every internal DOM-backed element came through. Moving the cursor produced a changing result, confirming the hit-test was genuinely traversing the UIA provider tree.

Then: maximize the overlay to fullscreen so the test application is fully covered.

```
[+] FindElementFromPointHelper: 0x1A106A, point: {X=2235,Y=584}
[+] Top window: 0x71424
[+] ElementFromPointHelper returned element: 0x20B4D5B570, isWebView: False
```

The result was a shallow, root-level element. Something had changed — and the only variable was whether the overlay covered test application or not.

---

### Chromium's Window Hierarchy: Two Trees, One App

To understand what happened, we need to look at how Electron (Chromium) structures its Win32 window hierarchy. Spying the window tree with Spy++ reveals two separate subtrees under the desktop:

```
1000C  Desktop (GetDesktopWindow)
└── 1A106A  Chrome_WidgetWin_1        ← the visible app window (shell)
    └── 71424   Intermediate D3D Window  ← GPU compositor placeholder
```

```
1000C  Desktop
└── 21088A  Chrome_WidgetWin_0        ← hidden message window
    ├── 82141C  Chrome Legacy Window - Chrome_RenderWidgetHostHWND
    └── 63131A  Chrome Legacy Window - Chrome_RenderWidgetHostHWND
```

The two `Chrome_RenderWidgetHostHWND` windows are the critical pieces. They host the actual UIA provider for the page's DOM — every button, every input, every element that UIA can see lives here. They are **not** children of the visible `Chrome_WidgetWin_1`; they live under the hidden `Chrome_WidgetWin_0`.

When the overlay covered only 1/4 of the screen and the test application was unobscured, `GetTopWindow(Chrome_WidgetWin_1)` returned `82141C` — the `RenderWidgetHostHWND`. That is the window with the UIA provider. `ElementFromPointHelper` could hit-test against it and return a deep element.

When the overlay covered the full screen and the test application was fully occluded, `GetTopWindow(Chrome_WidgetWin_1)` returned `71424` — the `Intermediate D3D Window`. That window has no UIA provider. `ElementFromPointHelper` ran into a dead end and returned the root.

**The overlay window caused `GetTopWindow` to return a different result.** That is the anomaly.

---

### The Mechanism: Chromium's Renderer Hibernation

Modern Chromium (and therefore all Electron apps) actively monitors whether its windows are occluded by other windows on screen. When it determines that its content is completely hidden behind an opaque, non-transparent foreign window, it initiates a **renderer hibernation**:

1. The GPU-accelerated render pipeline is suspended to save CPU and GPU resources.
2. Chromium performs a **dynamic reparenting**: the `Chrome_RenderWidgetHostHWND` — along with its attached UIA provider — is detached from `Chrome_WidgetWin_1` and moved under the hidden `Chrome_WidgetWin_0` message window.
3. Only the inert `Intermediate D3D Window` remains under `Chrome_WidgetWin_1` as a placeholder.

This is a performance optimization that is entirely reasonable from Chromium's perspective. But from ours, it is catastrophic: the UIA provider that backs all the interesting elements has been relocated to a subtree we are not querying, and what remains in the original subtree returns nothing useful.

The key insight is that Chromium uses **Windows DWM occlusion detection** to trigger this. Our Avalonia overlay window, being a fully opaque, non-layered Win32 window, registers as a complete occluder of the test application. Chromium sees this, concludes it is invisible to the user, and hibernates.

---

### First Workaround: Faking Transparency with `WS_EX_LAYERED`

If the trigger is DWM detecting an opaque occluder, the fix is to make the overlay appear non-opaque to DWM — even if visually it still looks solid to the user.

```csharp
var exStyle = PInvoke.GetWindowLong((HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
PInvoke.SetWindowLong(
    (HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
    exStyle | (int)WINDOW_EX_STYLE.WS_EX_LAYERED);
PInvoke.SetLayeredWindowAttributes(
    (HWND)hWnd, new COLORREF(0), 254,
    LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
```

Setting `WS_EX_LAYERED` with alpha = 254 (not 255) is enough: DWM no longer classifies the window as a full opaque occluder. Chromium's hibernation logic sees a "transparent" window and keeps its `RenderWidgetHostHWND` attached and live.

This worked for Chromium/Electron apps. But a different class of windows — pure Win32 owner-draw applications — still could not be deeply picked. Those windows had nothing to do with Chromium; the problem lay elsewhere.

---

### The Ultimate Solution: `UIA_WindowVisibilityOverridden`

Digging further into `UIAutomationCore.dll` with IDA, the visibility-checking routine that UIA uses when traversing the window tree is `BasicHwndUtils::GetWindowVisibility`:

```cpp
__int64 __fastcall BasicHwndUtils::GetWindowVisibility(HWND a1)
{
    HANDLE PropW = GetPropW(a1, L"UIA_WindowVisibilityOverridden");
    if (PropW == HANDLE_FLAG_INHERIT)           // == 1
        goto LABEL_18;                          // treat as visible
    if (PropW == HANDLE_FLAG_PROTECT_FROM_CLOSE) // == 2
        return 0;                               // treat as invisible

    // ... normal visibility checks (IsWindowVisible, GetWindowRect,
    //     DwmGetWindowAttribute DWMWA_CLOAKED) ...
}
```

The very first thing the function does is check the **window property** `UIA_WindowVisibilityOverridden` via `GetPropW`. This is an undocumented but stable hook:

| Property value                         | Effect on UIA traversal                                       |
| -------------------------------------- | ------------------------------------------------------------- |
| `1` (`HANDLE_FLAG_INHERIT`)            | Force-treat as visible, skip all other checks                 |
| `2` (`HANDLE_FLAG_PROTECT_FROM_CLOSE`) | Force-treat as **invisible** — UIA skips this window entirely |
| absent                                 | Normal visibility logic applies                               |

Setting this property to `2` on all overlay windows tells `UIAutomationCore` to ignore them completely during `ElementFromPoint` hit-testing. The overlay does not exist as far as UIA is concerned. The standard global `IUIAutomation::ElementFromPoint` walks straight past our windows and finds the element in the application behind them.

```csharp
// Applied to each overlay window (mask windows, tooltip, session window):
PInvoke.SetProp(hWnd, "UIA_WindowVisibilityOverridden",
    (HANDLE)(nint)2 /* HANDLE_FLAG_PROTECT_FROM_CLOSE */);
```

And cleaned up on close:

```csharp
PInvoke.RemoveProp(hWnd, "UIA_WindowVisibilityOverridden");
```

---

### Why This Is the Right Solution

This approach is superior to everything that came before it:

| Approach                             | Verdict                                                                                          |
| ------------------------------------ | ------------------------------------------------------------------------------------------------ |
| `WS_EX_TRANSPARENT` + global hooks   | Works for picking, but breaks normal input handling entirely                                     |
| Scoped `ElementFromPointHelper`      | Avoids the overlay visibility problem at the API level, but exposes the Chromium hibernation bug |
| `WS_EX_LAYERED` alpha=254            | Fixes Chromium hibernation, but does not help pure Win32 owner-draw windows                      |
| `UIA_WindowVisibilityOverridden = 2` | UIA itself ignores the overlay windows; no hibernation trigger, no scoped-API workaround needed  |

With `UIA_WindowVisibilityOverridden` set, the original `IUIAutomation::ElementFromPoint` — the standard, fully documented, publicly supported API — works correctly. There is no reverse engineering, no private function pointer, no DLL scanning. The entire `UIAutomationHelper` scaffolding from Chapter 3 becomes unnecessary for the picking use case.

There is also no visual side effect. The alpha=254 layered trick requires the overlay to actually render as slightly transparent from DWM's perspective; `UIA_WindowVisibilityOverridden` is purely a metadata property and has zero effect on rendering, compositing, or hit-test routing for normal mouse events.

---

### The Chromium Hibernation Diagram

```
                      ┌─────────────────────────────────────┐
                      │          Without overlay            │
                      │   (or with UIA_WVO=2 on overlay)    │
                      └──────────────────┬──────────────────┘
                                         │
           Desktop (1000C)               │
           └── Chrome_WidgetWin_1 (1A106A) ← GetTopWindow returns this's child:
               └── Intermediate D3D      │
                                         ▼
           Desktop (1000C)            RenderWidgetHostHWND (82141C)
           └── Chrome_WidgetWin_0       ← lives here, has UIA provider
               ├── RenderWidgetHostHWND (82141C)  ✓ ElementFromPoint succeeds
               └── RenderWidgetHostHWND (63131A)


                      ┌─────────────────────────────────────┐
                      │      With fully opaque overlay      │
                      │  (no UIA_WVO, Chromium hibernates)  │
                      └──────────────────┬──────────────────┘
                                         │
           Desktop (1000C)               │
           └── Chrome_WidgetWin_1 (1A106A) ← GetTopWindow returns:
               └── Intermediate D3D (71424)  ✗ no UIA provider → root element only
                                         │
           Desktop (1000C)               ▼
           └── Chrome_WidgetWin_0 (21088A)  ← renderer moved here by Chromium
               ├── RenderWidgetHostHWND (82141C)  (hidden, no longer queried)
               └── RenderWidgetHostHWND (63131A)
```

---

### Summary

The final obstacle was not a UIA API limitation but a Chromium performance optimization colliding with our overlay design. A fullscreen opaque window triggers renderer hibernation in Chromium, which dynamically reparents the UIA provider away from the window tree we were querying.

The `UIA_WindowVisibilityOverridden` window property — an undocumented but consistent internal hook inside `UIAutomationCore.dll` — provides the cleanest possible fix: it instructs UIA's own traversal logic to skip the overlay entirely, as if it were not there. The result is that `IUIAutomation::ElementFromPoint` works correctly with no scoping tricks, no private APIs, and no rendering side effects.

This property exists, it is stable across Windows versions, and it is the right tool for the job.
