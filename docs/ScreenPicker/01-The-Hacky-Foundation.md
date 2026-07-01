# Scoped UI Automation Element Picking: From Hacks to Reverse Engineering

## Chapter 1: The Hacky Foundation — Why We Ended Up Here

### Background

Everywhere's screen-picker feature lets the user hover over any UI element on screen, highlight it with a mask overlay, and click to "capture" it as a target for AI actions. This sounds simple. It was not.

The picker needs three things simultaneously:
1. A visual overlay covering the entire virtual screen (the mask + highlight).
2. Real-time detection of which UI Automation element is under the cursor.
3. Correct interception of mouse/keyboard events so the user can confirm or cancel without accidentally clicking through to the desktop.

Getting all three to coexist without fighting each other drove the first implementation into a corner that would take months to fully escape.

---

### The Fundamental Tension: Overlay vs. ElementFromPoint

The root cause of every hack in the original implementation can be traced to a single API limitation.

`IUIAutomation::ElementFromPoint` — the standard Windows UI Automation API for hit-testing — is **global**. It finds the topmost element at a given screen coordinate across the entire desktop. There is no public `IUIAutomationElement::ElementFromPoint` or scoped variant that restricts the search to a specific window's subtree.

This means: **whatever window is topmost at the cursor position is what UIA will pick.** If we put a fullscreen overlay window on top, `ElementFromPoint` returns a UIA element belonging to *our* overlay, not the application underneath. We tried disabling UIA on our window (`WS_EX_NOACTIVATE`, UIA provider suppression) — the result was just an empty UIA node with no children. Still our window.

The only way out appeared to be making the overlay window **mouse-transparent** (`WS_EX_TRANSPARENT`). With `WS_EX_TRANSPARENT`, Windows routes hit-testing through the transparent window to whatever is behind it, so `ElementFromPoint` finally sees the target application's elements.

But mouse-transparency means the overlay window receives **no mouse events**. We need those events to:
- Show the tooltip following the cursor
- Detect left-click (confirm selection) and right-click (cancel)
- Change pick mode with the scroll wheel
- Block all mouse input from reaching the desktop (clicking a button while trying to pick it would be catastrophic)

So we needed another mechanism to intercept mouse events. Enter the global low-level hook.

---

### The Original Architecture

The first working implementation used this chain of workarounds:

```
Window shows (ScreenSelectionSession)
  └─► OnPointerEntered
        └─► SendInput(MOUSEEVENTF_RIGHTDOWN)   ← HACK #1
              └─► OnPointerPressed (triggered by synthetic event)
                    ├─► SetHitTestVisible(this, false)  [WS_EX_TRANSPARENT + WS_DISABLED + WS_EX_LAYERED]
                    ├─► MaskWindows.Show()
                    ├─► LowLevelHook.CreateMouseHook(...)   ← HACK #2
                    └─► LowLevelHook.CreateKeyboardHook(...)
```

**HACK #1: Synthetic Right-Click for Cursor Lock**

The moment the session window appears, we inject a fake `MOUSEEVENTF_RIGHTDOWN` via `SendInput`. This has two effects:
- It triggers `OnPointerPressed` (the window still has input at this exact moment, before we set it transparent), which kicks off the rest of the initialization.
- More importantly: injecting a mouse button *down* without a corresponding *up* causes Windows to enter a "mouse capture" state anchored to our window. This prevents the OS cursor from changing shape in response to hover effects on underlying windows (e.g., the resize arrow when hovering a window edge).

Without this, the cursor would flicker between the crosshair and whatever cursor the underlying application requested, which looks terrible.

**HACK #2: WH_MOUSE_LL for Everything Else**

Since the overlay is now transparent to input, normal Avalonia mouse events stop arriving. We install a global low-level mouse hook (`WH_MOUSE_LL`) to compensate. The hook:
- `blockNext = true` on all events except `WM_MOUSEMOVE` — prevents any click from reaching the desktop
- On `WM_MOUSEMOVE`: updates the tooltip position and calls `PickElement()`
- On `WM_LBUTTONUP`: confirms the selection and closes the session
- On `WM_RBUTTONUP`: cancels the selection (and triggers the shutdown sequence)
- On `WM_MOUSEWHEEL`: cycles the pick mode (Screen / Window / Element)

**The Shutdown Sequence — Anti-Phantom-Click**

Closing the session is equally fraught. We injected a `MOUSEEVENTF_RIGHTDOWN` at startup; we must inject `MOUSEEVENTF_RIGHTUP` at shutdown to balance it. But if the overlay is still transparent when we send `RightUp`, that event falls through to the desktop — Explorer sees a complete right-button sequence and opens a context menu.

The shutdown sequence became:
1. `e.Cancel = true` (suppress the close)
2. Dispose the hooks (stop intercepting events)
3. `SetHitTestVisible(this, true)` — make the overlay opaque to input again
4. `Dispatcher.UIThread.Post(...)` — wait one frame for the style change to take effect
5. `SendInput(MOUSEEVENTF_RIGHTUP)` — now the overlay will absorb it
6. `Dispatcher.UIThread.Post(Close, DispatcherPriority.Background)` — finally close

Two levels of async dispatch just to close a window.

---

### The Pain Points

This architecture was functional under normal conditions. Under abnormal conditions it was fragile:

**UAC Elevation**: When a User Account Control dialog appears, the global hook stops receiving messages (the hook runs in our process which has standard integrity; the UAC window runs at high integrity). The hook goes silent while the right mouse button is synthetically held down. The button stays "pressed" indefinitely. The user's right mouse button is now effectively broken until they click it manually.

**Third-Party Mouse Tools**: Tools like MouseInc, X-Mouse Button Control, and AutoHotkey also install `WH_MOUSE_LL` hooks. When they see an `RBUTTONDOWN` with no matching `RBUTTONUP` (we blocked the up event), or when our hook fires before theirs and eats the event, behavior becomes unpredictable.

**Stutters and Lag**: The low-level hook runs on a dedicated high-priority STA thread (by design, to meet Windows' hook timeout requirements). Any cross-thread dispatch to update the UI adds latency. On some machines with busy background processes, the 300ms hook timeout would occasionally fire, silently aborting the hook callback.

**Code Complexity**: The combination of synthetic input, hook state, and the multi-stage shutdown sequence made the code extremely difficult to reason about or modify safely. Adding features like "free rectangular selection" required touching six different places.

The real fix required solving the root problem: `ElementFromPoint` seeing our window instead of the target.

---

*Continue reading: [Chapter 2 — The Search for a Scoped ElementFromPoint](./02-The-Search-For-Scoped-ElementFromPoint.md)*
