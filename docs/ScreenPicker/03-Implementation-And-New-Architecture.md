# Scoped UI Automation Element Picking: From Hacks to Reverse Engineering

## Chapter 3: Implementation and the New Architecture

### The Scanner: `UIAutomationHelper.FindElementFromPointHelper`

The function locator is implemented in `UIAutomationHelper.cs` and runs once at static initialization time. Its cost is paid upfront — roughly 20–50ms on first call (a linear scan over ~3MB of DLL memory) — and the resulting function pointer is cached for the lifetime of the process.

```csharp
private static nint FindElementFromPointHelper()
{
    var hModule = NativeLibrary.Load("UIAutomationCore.dll");
    var moduleSize = GetModuleMemorySize("UIAutomationCore.dll");
    var moduleBytes = new Span<byte>((void*)hModule, moduleSize);

    // Step 1: Find the telemetry string
    var patternBytes = "CUIAutomation::ElementFromPointHelper\0"u8;
    var stringOffset = moduleBytes.IndexOf(patternBytes);
    var stringVa = (ulong)hModule + (ulong)stringOffset;

    // Step 2: Find the LEA instruction that loads this string into RDX
    var decoder = Decoder.Create(64, new StreamCodeReader(...));
    decoder.IP = (ulong)hModule;
    ulong leaInstructionIP = 0;
    while (decoder.IP < (ulong)hModule + (ulong)moduleSize)
    {
        decoder.Decode(out var instruction);
        if (instruction is { Mnemonic: Mnemonic.Lea, IsIPRelativeMemoryOperand: true }
            && instruction.IPRelativeMemoryAddress == stringVa)
        {
            leaInstructionIP = instruction.IP;
            break;
        }
    }

    // Step 3: Walk backward to the function prologue (past 0xCC padding)
    var currentOffset = (int)(leaInstructionIP - (ulong)hModule);
    while (currentOffset > 0)
    {
        if (moduleBytes[currentOffset - 1] == 0xCC && moduleBytes[currentOffset - 2] == 0xCC)
            break;
        currentOffset--;
    }

    return hModule + currentOffset;
}
```

The [Iced](https://github.com/icedland/iced) disassembler library handles the instruction decoding, giving us proper x86-64 decode semantics without hand-rolling RIP-relative address calculation.

---

### The Caller: `UIAutomationHelper.ElementFromPointer`

With the function pointer in hand, calling it is straightforward. The signature as reconstructed from IDA:

```cpp
HRESULT CUIAutomation::ElementFromPointHelper(
    CUIAutomation *this,
    IUiaNode *clientRootNode,
    tagPOINT point,
    IUIAutomationElement **result,
    bool *isWebView
);
```

Translated to C# unsafe code:

```csharp
public static IUIAutomationElement? ElementFromPointer(IUIAutomation automation, HWND hWnd, Point point)
{
    if (ElementFromPointHelperPtr == 0) return null;

    // Build a scoped root node from the target window
    var hResult = (HRESULT)UiaNodeFromHandle(hWnd, out var hNode);
    if (hResult.Failed) return null;

    var fn = (delegate* unmanaged[Cdecl]<nint, nint, Point, nint*, bool*, HRESULT>)
             ElementFromPointHelperPtr;

    nint pElement = 0;
    bool isWebView = false;
    hResult = fn(Marshal.GetIUnknownForObject(automation), hNode, point, &pElement, &isWebView);
    if (hResult.Failed || pElement == 0) return null;

    // WebView second pass: the result is a CEF/WebView2 host; recurse into it
    if (isWebView)
    {
        hResult = (HRESULT)Marshal.QueryInterface(
            pElement, typeof(IInternalAutomationElement).GUID, out hNode);
        if (hResult.Failed)
            return Marshal.GetObjectForIUnknown(pElement) as IUIAutomationElement; // fallback

        hResult = fn(Marshal.GetIUnknownForObject(automation), hNode, point, &pElement, &isWebView);
        if (hResult.Failed || pElement == 0) return null;
    }

    return Marshal.GetObjectForIUnknown(pElement) as IUIAutomationElement;
}
```

A few implementation details worth noting:

**`IInternalAutomationElement`**: When `isWebView == true`, we need to call `ElementFromPointHelper` again. The returned `IUIAutomationElement` actually implements an undocumented internal interface (IID `0A87E528-22BA-4D49-9CD5-147526282441`) with method `GetUiaNode(out nint node)`. This converts the `IUIAutomationElement` back into a `IUiaNode`, which can then be passed to `ElementFromPointHelper` for a second call scoped to the WebView's content frame. The GUID was found by examining the `QueryInterface` call inside `ElementFromPointHelper` itself.

**`[GeneratedComInterface]`**: The `IInternalAutomationElement` declaration uses the .NET 8 `[GeneratedComInterface]` + `[LibraryImport]` source-generated P/Invoke path, avoiding the legacy `[ComImport]` overhead.

**`UiaNodeFromHandle`**: This is a documented (though legacy) export from `UIAutomationCore.dll`. It is the bridge between the Win32 `HWND` world and the `IUiaNode` world, and it is the only public API we use that was not part of the original design.

---

### Warm-Up and the Static Constructor

One subtlety: `UIAutomationCore.dll` is not loaded until the first UIA call is made. If we call `FindElementFromPointHelper` before any UIA object has been created, `NativeLibrary.Load` will load the DLL fresh — but the scan still works correctly because we're loading the same DLL the UIA COM server will later use. FlaUI's `UIA3Automation` constructor triggers the first real UIA call, which in turn ensures the DLL is loaded with the correct identity.

To avoid any ordering ambiguity, we added a warm-up call in `VisualElementContext`'s static constructor:

```csharp
static VisualElementContext()
{
    // Warm up UIAutomationHelper so ElementFromPointHelperPtr is resolved
    // before the first interactive pick session begins.
    UIAutomationHelper.ElementFromPointer(
        Automation.NativeAutomation,
        PInvoke.GetForegroundWindow(),
        new Point(100, 100));
}
```

This runs once during application startup (on a background thread, before any picker window is shown), so the 20–50ms scanning cost is invisible to the user.

---

### The New ScreenSelectionSession Architecture

With a working scoped `ElementFromPoint`, the entire premise of the old architecture changes. The overlay window no longer needs to be mouse-transparent. It sits on top, opaque to input, and handles mouse/keyboard events through normal Avalonia mechanisms.

**What changes:**
- The `ScreenSelectionTransparentWindow` (the main session window) **no longer calls `SetHitTestVisible(this, false)`**. It stays solid to input throughout the session.
- `SendInput(MOUSEEVENTF_RIGHTDOWN)` is gone — cursor lock was only needed because the window was transparent. A normal opaque window on top naturally holds cursor focus.
- `LowLevelHook.CreateMouseHook` is gone — normal `PointerMoved`, `PointerPressed`, and `PointerWheelChanged` Avalonia events work correctly.
- The `WH_KEYBOARD_LL` hook is gone — the session window can receive `KeyDown` events directly (it is the topmost focusable window).
- The complex `OnClosing` shutdown sequence is gone — no phantom right-click to absorb, no async dispatch chain. Just close.

**What stays the same:**
- Each `ScreenSelectionMaskWindow` **keeps `SetHitTestVisible(false)`** — the masks are purely visual, and mouse-transparency here is harmless (events go to the session window above, not through the mask).
- The `ToolTipWindow` keeps `SetHitTestVisible(false)` — it is informational only.
- `EnumWindows` / `WindowFromPoint` is used to identify the target window behind the overlay before calling `UIAutomationHelper.ElementFromPointer`.

**`PickElement` in Element mode:**

```csharp
case ScreenSelectionMode.Element:
{
    var targetHWnd = PInvoke.WindowFromPoint(cursorPos);
    if (targetHWnd == HWND.Null) break;
    var rootHWnd = PInvoke.GetAncestor(targetHWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
    if (rootHWnd == HWND.Null) break;

    // Skip our own overlay windows
    if (_ownWindows.Contains(rootHWnd)) break;

    var nativeElement = UIAutomationHelper.ElementFromPointer(
        (IUIAutomation)Automation.NativeAutomation,
        rootHWnd,
        cursorPos);
    PickingElement = nativeElement != null
        ? TryCreateVisualElement(() => Automation.FromHandle(rootHWnd))  // wrap in FlaUI
        : null;
    break;
}
```

Wait — `WindowFromPoint` on a mouse-transparent window returns the window *behind* it (that is what `WS_EX_TRANSPARENT` does). But now our session window is **opaque to input**, so `WindowFromPoint` will return the session window's HWND. We therefore enumerate the overlay HWNDs collected at session start and skip them:

```csharp
var allOwnHwnds = new HashSet<HWND>(
    MaskWindows.Select(w => (HWND)(w.TryGetPlatformHandle()?.Handle ?? 0))
        .Append((HWND)(TryGetPlatformHandle()?.Handle ?? 0))
        .Append((HWND)(ToolTipWindow.TryGetPlatformHandle()?.Handle ?? 0))
        .Where(h => h != HWND.Null));
```

Then `WindowFromPoint` is called, and if the result is one of our own windows, we use `EnumWindows` to find the topmost non-own window at that point.

---

### Result

The new code path for `ScreenSelectionMode.Element`:

```
Mouse moves (Avalonia PointerMoved event)
  └─► PickElement(cursorPos)
        └─► FindWindowBehindOwnOverlays(cursorPos)      [EnumWindows, skip own HWNDs]
              └─► UIAutomationHelper.ElementFromPointer(automation, targetHWnd, point)
                    ├─► UiaNodeFromHandle(targetHWnd)   [documented legacy API]
                    └─► ElementFromPointHelper(this, node, point, &result, &isWebView)
                          └─► [WebView second pass if needed]
                    return IUIAutomationElement*
              └─► FlaUI wrap → AutomationVisualElementImpl
        └─► MaskWindows.SetMask(PickingElement.BoundingRectangle)
```

No global hooks. No synthetic input. No async shutdown chain. The session window behaves like any other normal Avalonia window — it opens, handles events, and closes. The only unconventional piece is the function pointer obtained by scanning DLL memory, and that is isolated entirely within `UIAutomationHelper` with a graceful `null` fallback path.

---

### Summary Table

| Concern                    | Old Approach                  | New Approach                               |
| -------------------------- | ----------------------------- | ------------------------------------------ |
| Overlay input transparency | `WS_EX_TRANSPARENT`           | Normal opaque window                       |
| Cursor lock                | `SendInput(RIGHTDOWN)`        | N/A (opaque window holds focus)            |
| Mouse event capture        | `WH_MOUSE_LL` global hook     | Avalonia `PointerMoved` / `PointerPressed` |
| Keyboard event capture     | `WH_KEYBOARD_LL` global hook  | Avalonia `KeyDown`                         |
| Session close              | 6-step async sequence         | `Close()`                                  |
| `ElementFromPoint` scope   | Global (sees own overlay)     | Scoped to target window                    |
| WebView support            | Not handled                   | Built into `ElementFromPointHelper`        |
| UAC resilience             | Hooks go silent, mouse locks  | No hooks, no issue                         |
| Third-party tool conflict  | Hook ordering fights          | N/A                                        |
| Implementation complexity  | ~300 lines with 6 workarounds | ~150 lines, straightforward                |

The one trade-off is that `UIAutomationHelper` relies on an undocumented internal function reached via memory scanning. If Microsoft ever renames or removes the telemetry string `"CUIAutomation::ElementFromPointHelper"`, the scanner returns 0 and the code falls back to the old `Automation.FromPoint()` path — globally scoped, but functional. The risk is low: this string has been present in every version of `UIAutomationCore.dll` from Windows 7 through Windows 11 24H2, and ETW telemetry instrumentation is not something Microsoft removes from shipping code.

---

### Full Code

```csharp
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Iced.Intel;
using Interop.UIAutomationClient;
using WinRT;
using ZLinq;
using Decoder = Iced.Intel.Decoder;

namespace Everywhere.Windows.Interop;

internal static unsafe class UIAutomationHelper
{
    private static readonly nint ElementFromPointHelperPtr = FindElementFromPointHelper();

    public static IUIAutomationElement? ElementFromPointer(IUIAutomation automation, HWND hWnd, Point point)
    {
        if (ElementFromPointHelperPtr == 0) return null;

        if (hWnd.IsNull) return null;
        Debug.WriteLine($"[+] FindElementFromPointHelper: 0x{(nint)hWnd:X}, point: {point}");
        hWnd = PInvoke.GetTopWindow(hWnd);
        Debug.WriteLine($"[+] Top window: 0x{(nint)hWnd:X}");

        IUIAutomationElement? rootElement;
        try
        {
            rootElement = automation.ElementFromHandle(hWnd);
            if (rootElement == null)
            {
                Debug.WriteLine("[-] ElementFromHandle failed to get root element.");
                return null;
            }
        }
        catch
        {
            return null;
        }

        var pRootElement = Marshal.GetIUnknownForObject(rootElement);
        if (TryToUiaNode(pRootElement, out var pUiaNode).Failed)
        {
            Debug.WriteLine("[-] Failed to get UiaNode from root element. Fallback to root element.");
            return rootElement; // Fallback to root element if we can't get the UiaNode
        }

        // CUIAutomation *this,
        // struct IUiaNode *node,
        // struct tagPOINT point,
        // struct IUIAutomationElement **result,
        // bool *isWebView
        var pAutomation = Marshal.GetIUnknownForObject(automation);
        var elementFromPointHelper = (delegate* unmanaged[Stdcall]<nint, nint, Point, nint*, bool*, HRESULT>)ElementFromPointHelperPtr;
        nint pAutomationElement = 0;
        var isWebView = false;
        var hResult = elementFromPointHelper(pAutomation, pUiaNode, point, &pAutomationElement, &isWebView);
        if (hResult.Failed || pAutomationElement == 0)
        {
            Debug.WriteLine($"[-] ElementFromPointHelper failed. HRESULT: {hResult}, pAutomationElement: 0x{pAutomationElement:X}");
            return null;
        }

        Debug.WriteLine($"[+] ElementFromPointHelper returned element: 0x{pAutomationElement:X}, isWebView: {isWebView}");

        // According to ElementFromPointHelper, if it's a webview, we need to convert it to UiaNode and run again
        if (isWebView)
        {
            if (TryToUiaNode(pAutomationElement, out var pWebViewNode).Failed)
            {
                Debug.WriteLine("[-] Failed to get UiaNode from WebView element. Fallback to WebView element.");
                return Marshal.GetObjectForIUnknown(pAutomationElement) as IUIAutomationElement;
            }

            hResult = elementFromPointHelper(pAutomation, pWebViewNode, point, &pAutomationElement, &isWebView);
            if (hResult.Failed || pAutomationElement == 0)
            {
                Debug.WriteLine($"[-] Failed to convert WebView node to UiaNode. HRESULT: {hResult}");
                return null;
            }

            Debug.WriteLine(
                $"[+] After converting WebView node, ElementFromPointHelper returned element: 0x{pAutomationElement:X}, isWebView: {isWebView}");
        }

        return Marshal.GetObjectForIUnknown(pAutomationElement) as IUIAutomationElement;
    }

    private static HRESULT TryToUiaNode(nint pElement, out nint pUiaNode)
    {
        pUiaNode = 0;

        var hResult = (HRESULT)Marshal.QueryInterface(pElement, typeof(IInternalAutomationElement).GUID, out var pInternal);
        if (hResult.Failed || pInternal == 0)
        {
            Debug.WriteLine($"[-] QueryInterface for IInternalAutomationElement failed. HRESULT: {hResult}, pInternal: 0x{pInternal:X}");
            return hResult; // fallback to original element
        }

        var internalAutomationElement = ComWrappersSupport.CreateRcwForComObject<IInternalAutomationElement>(pInternal);
        if (internalAutomationElement is null)
        {
            Debug.WriteLine("[-] Failed to create RCW for IInternalAutomationElement.");
            return hResult; // fallback to original element
        }

        hResult = (HRESULT)internalAutomationElement.GetUiaNode(out pUiaNode);
        if (hResult.Failed || pUiaNode == 0)
        {
            Debug.WriteLine($"[-] GetUiaNode failed. HRESULT: {hResult}, hNode: 0x{pUiaNode:X}");
            return hResult; // fallback to original element
        }

        return hResult;
    }

    private static nint FindElementFromPointHelper()
    {
        const string moduleName = "UIAutomationCore.dll";
        var hModule = NativeLibrary.Load("UIAutomationCore.dll");

        if (hModule == 0)
        {
            Console.WriteLine("[-] 未加载 uiautomationcore.dll，请确保目标进程已初始化 UIAutomation。");
            return 0;
        }

        var moduleSize = GetModuleMemorySize(moduleName);
        if (moduleSize == 0) return 0;

        var moduleBytes = new Span<byte>((void*)hModule, moduleSize);
        var patternBytes = "CUIAutomation::ElementFromPointHelper\0"u8;
        var stringOffset = moduleBytes.IndexOf(patternBytes);
        if (stringOffset == -1)
        {
            Console.WriteLine("[-] 未在内存中找到目标遥测字符串。");
            return 0;
        }

        var stringVa = (ulong)hModule + (ulong)stringOffset;

        Console.WriteLine($"[+] 找到特征字符串地址: 0x{stringVa:X}");

        // search for LEA RDX, [RIP + offset] that references the string
        var codeReader = new StreamCodeReader(new UnmanagedMemoryStream((byte*)hModule, moduleSize));
        var decoder = Decoder.Create(64, codeReader);
        decoder.IP = (ulong)hModule;

        ulong leaInstructionIP = 0;
        while (decoder.IP < (ulong)hModule + (ulong)moduleSize)
        {
            decoder.Decode(out var instruction);

            if (instruction is not { Mnemonic: Mnemonic.Lea, IsIPRelativeMemoryOperand: true }) continue;
            if (instruction.IPRelativeMemoryAddress != stringVa) continue;

            leaInstructionIP = instruction.IP;
            break;
        }

        if (leaInstructionIP == 0)
        {
            Console.WriteLine("[-] 未找到引用该字符串的 LEA 指令。");
            return 0;
        }

        // 4. search for function Prologue
        var currentOffset = (int)(leaInstructionIP - (ulong)hModule);
        while (currentOffset > 0)
        {
            // double 0xCC means padding between functions
            if (moduleBytes[currentOffset - 1] == 0xCC && moduleBytes[currentOffset - 2] == 0xCC)
            {
                break;
            }

            currentOffset--;
        }

        return hModule + currentOffset;
    }

    private static int GetModuleMemorySize(string moduleName)
    {
        return Process.GetCurrentProcess().Modules
            .AsValueEnumerable()
            .Cast<ProcessModule>()
            .Where(module => module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            .Select(module => module.ModuleMemorySize)
            .FirstOrDefault();
    }
}

[GeneratedComInterface]
[Guid("0A87E528-22BA-4D49-9CD5-147526282441")]
internal partial interface IInternalAutomationElement
{
    // (*(_QWORD *)v4 + 24LL)
    [PreserveSig]
    int GetUiaNode(out nint node);
}
```

---

*Continue reading: [Chapter 4 — The Overlay Occlusion Problem](./04-The-Overlay-Occlusion-Problem.md)*