# Scoped UI Automation Element Picking: From Hacks to Reverse Engineering

## Chapter 2: The Search for a Scoped ElementFromPoint

### Mapping the Public API Surface

The obvious starting point was the official documentation. `IUIAutomation` has exactly one point-based hit-test method: `ElementFromPoint`. No overloads, no scoped variant. We checked every interface in the `IUIAutomation` family (`IUIAutomation2` through `IUIAutomation6`) — newer versions add methods like `CreateEventHandlerGroup` and `GetPropertyProgrammaticName`, but none add a window-scoped hit-test.

`IUIAutomationElement` itself has no hit-test method at all. It can navigate (parent, children, siblings), query properties, and invoke patterns — but it cannot be asked "which of your descendants is at this coordinate?"

The **provider-side** interface `IRawElementProviderFragmentRoot::ElementProviderFromPoint` does exactly what we want — it is scoped to a fragment root and returns the deepest provider at a given coordinate. But this lives in `uiautomationcore.h` and is only callable by a UIA *provider* (i.e., code running inside the application being automated). As a client, we cannot call another process's `IRawElementProviderFragmentRoot` directly.

We also looked at `IAccessible::accHitTest` (MSAA). It is scoped to a specific accessible object and recursively finds the child at a point. But MSAA has weaker coverage than UIA — many modern frameworks (WPF, WinUI, custom DirectX UIs) expose only UIA providers, not MSAA. Using `accHitTest` would mean maintaining two parallel code paths and getting worse results on half the applications we care about.

**Conclusion from public APIs:** There is no scoped `ElementFromPoint` in the UIA client API. The constraint is fundamental, not an oversight.

---

### The TreeWalker Fallback

If UIA won't tell us the element at a point within a window, we can build it ourselves: start from the window's UIA root and recursively walk the tree, checking each child's `BoundingRectangle.Contains(point)` until we reach the deepest match.

This is exactly what the Linux port of Everywhere already does using AT-SPI (`AtspiService::AtspiElementFromPoint`):

```csharp
private AtspiVisualElement? AtspiElementFromPoint(AtspiVisualElement? parent, PixelPoint point, out int depth, bool root = false)
{
    // ...
    foreach (var child in ElementChildren(parent._element).OrderByDescending(child => child.Order))
    {
        var found = AtspiElementFromPoint(child, point, out var subdepth);
        if (found != null && subdepth > maxDepth)
        {
            maxDepth = subdepth;
            foundChild = found;
        }
    }
    if (foundChild != null) { depth = maxDepth + 1; return foundChild; }
    if (rect.Contains(point) && ElementVisible(parent._element)) return parent;
    return null;
}
```

The algorithm is sound: traverse all children, take the deepest match (highest `depth`), which corresponds to the most specific leaf element.

However, on Windows, each UIA property access (`BoundingRectangle`, children navigation) is a **cross-process COM call**. A deep UI tree (e.g., a complex WPF data grid, or a Chromium-based app with hundreds of accessibility nodes) can have dozens of levels and hundreds of nodes. Walking the entire tree live, on every mouse-move event, would take tens to hundreds of milliseconds per update — the picker would feel like it was running through mud.

We could mitigate this with FlaUI's `CacheRequest` (batch-prefetch properties for an entire subtree in one round-trip), but even then, the worst-case complexity for deeply nested or virtualizing trees is hard to bound. More critically: for browsers (Chrome, Edge, Electron) the UIA tree is enormous. `accHitTest` on Chromium takes milliseconds for the same reason.

We shelved this as a fallback but kept looking for something better.

---

### The `DWMWA_CLOAK` Dead End

If we could momentarily make our overlay window invisible to UIA's hit-testing — just for the duration of a single `ElementFromPoint` call — we could keep the overlay opaque to mouse input (no more `WS_EX_TRANSPARENT`) and use the standard global `ElementFromPoint` without interference.

`DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, true, ...)` cloaks a window: DWM stops compositing it and it becomes invisible to the user. Does UIA respect cloaking?

Testing confirmed that UIA does skip cloaked windows in `ElementFromPoint`. Problem solved, right?

No. The API call completes synchronously, but the DWM cloaking effect takes effect asynchronously — DWM works on its own composition schedule. Between calling `DwmSetWindowAttribute(CLOAK=true)` and `ElementFromPoint` and `DwmSetWindowAttribute(CLOAK=false)`, several frame boundaries may pass. To the user, the overlay flickers: it disappears for one or two frames and then reappears. At 60 Hz that is 16–33ms of visible flicker per mouse-move event. Completely unacceptable.

We could try to suppress the flicker with `DWMWA_FREEZE_REPRESENTATION`, but that just freezes the *thumbnail* shown in taskbar/Alt-Tab — the main window composition still flickers.

---

### The IDA Detour: Confirming the Internal API Exists

At this point the question became: does `UIAutomationCore.dll` *internally* implement scoped element-from-point? If so, is there any way to reach it?

Loading `UIAutomationCore.dll` into IDA with public symbols (the PDB ships with Windows) revealed the answer immediately. The call graph inside `CUIAutomation::ElementFromPoint` is:

```
CUIAutomation::ElementFromPoint(this, point, result)
  ├─► CUIAutomation::GetClientRootNode(this, &clientRootNode)
  │     └─► BasicUiaUtils::IsDesktop() ? desktop_path : app_path
  │           both paths ultimately call UiaNodeFromHandle(GetDesktopWindow(), ...)
  └─► CUIAutomation::ElementFromPointHelper(this, clientRootNode, point, result, &isWebView)
```

`ElementFromPointHelper` is a private method with the signature:
```cpp
HRESULT CUIAutomation::ElementFromPointHelper(
    CUIAutomation *this,
    IUiaNode *clientRootNode,   // ← this is what scopes the search
    tagPOINT point,
    IUIAutomationElement **result,
    bool *isWebView             // ← handles Chromium/CEF automatically
);
```

The `clientRootNode` parameter is what controls the search scope. When `GetClientRootNode` calls `GetDesktopWindow()`, it creates a node representing the entire desktop — giving global results. If we could pass a node representing only our *target* window, the search would be scoped to that window.

Crucially, the legacy UIA API `UiaNodeFromHandle(HWND, HUIANODE*)` exposes exactly this: it creates a `HUIANODE` for any window. And inspection confirmed that `HUIANODE` is `IUiaNode*` — the same type `ElementFromPointHelper` expects.

The internal logic also shows that `isWebView` handling is built in: if the initial result is a WebView host element, the function performs a second pass by converting the result back to a `IUiaNode` via `IInternalAutomationElement::GetUiaNode` (IID `0A87E528-22BA-4D49-9CD5-147526282441`) and recursing. We get Chromium support for free.

The capability exists. It just isn't exported.

---

### Three Ways to Reach a Private Function

We evaluated three approaches to calling `ElementFromPointHelper`:

**Option A: Load symbols**
`SymFromName` via `DbgHelp` can look up a symbol by name from the PDB. But this requires downloading the PDB from the Microsoft symbol server on first run (hundreds of milliseconds, requires network) and takes ~50ms even when cached. For a function called on every mouse-move event, this is completely untenable.

**Option B: Hardcode vtable/struct offsets**
The IDA output showed a virtual call at `(*(*clientRootNode + 96LL))(...)` pattern. We could index into the `IUiaNode` vtable at offset 96 / 8 = slot 12. But `IUiaNode` is an undocumented internal COM interface with no IID, no published vtable layout, and no binary compatibility guarantee. Testing across Windows 10 1903, 21H2, and Windows 11 23H2 showed this offset has already shifted at least once.

**Option C: Telemetry string scan**
The most robust approach: exploit the fact that Windows DLLs are built with ETW telemetry call traces. Every non-trivial function in `UIAutomationCore.dll` contains a `ClientApiCallTrace::ctor` call that passes the function name as a literal string. `ElementFromPointHelper` is no exception — its prologue calls `ClientApiCallTrace::ctor` with the string `"CUIAutomation::ElementFromPointHelper\0"`.

The algorithm:
1. Get the base address of the already-loaded `UIAutomationCore.dll`.
2. Scan the module's memory for the UTF-8 string `CUIAutomation::ElementFromPointHelper\0`.
3. Note its virtual address; scan the code section for a `LEA Rdx, [RIP + offset]` instruction that references this exact address (the `a2` parameter to `ClientApiCallTrace::ctor` is passed in `RDX` on x64).
4. Walk backward from that instruction until two consecutive `0xCC` padding bytes are found — this is the inter-function alignment gap, so the next byte is the function prologue.

This is **fully deterministic** across Windows versions: the string is part of the binary (not a debug symbol), the `LEA` encoding is canonical, and function prologues are always aligned after `0xCC` padding. No symbol server, no version-specific offsets, no network dependency.

---

*Continue reading: [Chapter 3 — Implementation and the New Architecture](./03-Implementation-And-New-Architecture.md)*
