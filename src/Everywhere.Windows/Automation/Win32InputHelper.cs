using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Input;
using Everywhere.Windows.Extensions;
using Win32Point = System.Drawing.Point;

namespace Everywhere.Windows.Automation;

/// <summary>
/// Centralizes guarded Win32 input injection used by Windows Automation actions.
/// </summary>
/// <remarks>
/// Every emitted event carries <see cref="InjectedInputMagic" /> so the Scope guard can distinguish Everywhere input from physical and unrelated injected input.
/// </remarks>
public static class Win32InputHelper
{
    /// <summary>
    /// Identifies low-level keyboard and mouse input injected by Everywhere.
    /// </summary>
    /// <remarks>This stable value is a classifier for the active Scope guard, not a secret or security credential.</remarks>
    public const uint InjectedInputMagic = 0x45565752;

    /// <summary>
    /// Resolves the root-owner window that contains a native target window.
    /// </summary>
    /// <param name="windowHandle">The target native window handle.</param>
    /// <returns>The root-owner window handle.</returns>
    public static nint GetRootOwnerWindow(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new InvalidOperationException("The target element does not belong to a native window.");
        }

        var rootWindow = PInvoke.GetAncestor((HWND)windowHandle, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        if (rootWindow == HWND.Null)
        {
            throw new InvalidOperationException("The target element does not belong to a valid root window.");
        }

        return rootWindow;
    }

    /// <summary>
    /// Brings a root-owner window to the foreground and verifies that Windows accepted the request.
    /// </summary>
    /// <param name="rootWindowHandle">The root-owner window handle.</param>
    public static void EnsureForegroundWindow(nint rootWindowHandle)
    {
        var rootWindow = (HWND)rootWindowHandle;
        if (!HasRootOwner(PInvoke.GetForegroundWindow(), rootWindow))
        {
            PInvoke.SetForegroundWindow(rootWindow);
        }

        if (!HasRootOwner(PInvoke.GetForegroundWindow(), rootWindow))
        {
            throw new InvalidOperationException("Windows refused to bring the target element's window to the foreground.");
        }
    }

    /// <summary>
    /// Injects one keyboard gesture after normalizing physically held modifier state.
    /// </summary>
    /// <param name="keyGesture">The key and requested modifiers.</param>
    /// <remarks>
    /// Unrequested modifiers are released and deliberately not restored. Requested modifiers already down are preserved, and only modifiers pressed by this method are released after the main key.
    /// </remarks>
    public static void SendKeyGesture(KeyGesture keyGesture)
    {
        const KeyModifiers supportedModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;
        if ((keyGesture.KeyModifiers & ~supportedModifiers) != KeyModifiers.None)
        {
            throw new NotSupportedException($"The keyboard gesture contains unsupported modifiers: {keyGesture.KeyModifiers}.");
        }

        var virtualKey = keyGesture.Key.ToVirtualKey();
        if (virtualKey == 0)
        {
            throw new NotSupportedException($"The keyboard key '{keyGesture.Key}' cannot be injected on Windows.");
        }

        Span<INPUT> inputs = stackalloc INPUT[18];
        var inputCount = 0;
        ReleaseUnrequestedModifier(
            inputs,
            ref inputCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Control),
            VIRTUAL_KEY.VK_CONTROL,
            VIRTUAL_KEY.VK_LCONTROL,
            VIRTUAL_KEY.VK_RCONTROL);
        ReleaseUnrequestedModifier(
            inputs,
            ref inputCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Alt),
            VIRTUAL_KEY.VK_MENU,
            VIRTUAL_KEY.VK_LMENU,
            VIRTUAL_KEY.VK_RMENU);
        ReleaseUnrequestedModifier(
            inputs,
            ref inputCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Shift),
            VIRTUAL_KEY.VK_SHIFT,
            VIRTUAL_KEY.VK_LSHIFT,
            VIRTUAL_KEY.VK_RSHIFT);
        ReleaseUnrequestedModifier(
            inputs,
            ref inputCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Meta),
            default,
            VIRTUAL_KEY.VK_LWIN,
            VIRTUAL_KEY.VK_RWIN);

        Span<VIRTUAL_KEY> pressedModifiers = stackalloc VIRTUAL_KEY[4];
        var pressedModifierCount = 0;
        PressRequestedModifier(
            inputs,
            ref inputCount,
            pressedModifiers,
            ref pressedModifierCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Control),
            VIRTUAL_KEY.VK_CONTROL,
            VIRTUAL_KEY.VK_LCONTROL,
            VIRTUAL_KEY.VK_RCONTROL);
        PressRequestedModifier(
            inputs,
            ref inputCount,
            pressedModifiers,
            ref pressedModifierCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Alt),
            VIRTUAL_KEY.VK_MENU,
            VIRTUAL_KEY.VK_LMENU,
            VIRTUAL_KEY.VK_RMENU);
        PressRequestedModifier(
            inputs,
            ref inputCount,
            pressedModifiers,
            ref pressedModifierCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Shift),
            VIRTUAL_KEY.VK_SHIFT,
            VIRTUAL_KEY.VK_LSHIFT,
            VIRTUAL_KEY.VK_RSHIFT);
        PressRequestedModifier(
            inputs,
            ref inputCount,
            pressedModifiers,
            ref pressedModifierCount,
            keyGesture.KeyModifiers.HasFlag(KeyModifiers.Meta),
            default,
            VIRTUAL_KEY.VK_LWIN,
            VIRTUAL_KEY.VK_RWIN);

        inputs[inputCount++] = CreateKeyboardInput(virtualKey, false);
        inputs[inputCount++] = CreateKeyboardInput(virtualKey, true);
        for (var i = pressedModifierCount - 1; i >= 0; i--) inputs[inputCount++] = CreateKeyboardInput(pressedModifiers[i], true);

        Send(inputs[..inputCount], "keyboard");
    }

    /// <summary>
    /// Moves the pointer to a validated physical screen point and injects one left click.
    /// </summary>
    /// <param name="point">The physical screen point.</param>
    /// <param name="rootWindowHandle">The root-owner window that must currently expose the point.</param>
    public static void Click(PixelPoint point, nint rootWindowHandle)
    {
        var screenLeft = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var screenTop = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var screenWidth = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        var screenHeight = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        if (screenWidth <= 0 ||
            screenHeight <= 0 ||
            point.X < screenLeft ||
            point.Y < screenTop ||
            point.X >= (long)screenLeft + screenWidth ||
            point.Y >= (long)screenTop + screenHeight)
        {
            throw new InvalidOperationException("The clickable point is outside the virtual screen bounds.");
        }

        var rootWindow = (HWND)rootWindowHandle;
        var pointWindow = PInvoke.WindowFromPoint(new Win32Point(point.X, point.Y));
        if (!HasRootOwner(pointWindow, rootWindow))
        {
            throw new InvalidOperationException("The clickable point is not currently exposed by the target element's root window.");
        }

        var normalizedX = screenWidth == 1 ? 0 : checked((int)(((long)point.X - screenLeft) * 65_535 / (screenWidth - 1)));
        var normalizedY = screenHeight == 1 ? 0 : checked((int)(((long)point.Y - screenTop) * 65_535 / (screenHeight - 1)));
        Span<INPUT> inputs =
        [
            CreateMouseInput(
                normalizedX,
                normalizedY,
                MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK),
            CreateMouseInput(0, 0, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN),
            CreateMouseInput(0, 0, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP),
        ];

        // TODO: Drive the virtual cursor when the interaction guard is implemented.
        Send(inputs, "mouse");
    }

    private static void ReleaseUnrequestedModifier(
        Span<INPUT> inputs,
        ref int inputCount,
        bool isRequested,
        VIRTUAL_KEY aggregateKey,
        VIRTUAL_KEY leftKey,
        VIRTUAL_KEY rightKey)
    {
        if (isRequested)
        {
            return;
        }

        var isLeftDown = IsKeyDown(leftKey);
        var isRightDown = IsKeyDown(rightKey);
        if (isLeftDown) inputs[inputCount++] = CreateKeyboardInput(leftKey, true);
        if (isRightDown) inputs[inputCount++] = CreateKeyboardInput(rightKey, true);
        if (!isLeftDown && !isRightDown && aggregateKey != default && IsKeyDown(aggregateKey))
        {
            inputs[inputCount++] = CreateKeyboardInput(aggregateKey, true);
        }
    }

    private static void PressRequestedModifier(
        Span<INPUT> inputs,
        ref int inputCount,
        Span<VIRTUAL_KEY> pressedModifiers,
        ref int pressedModifierCount,
        bool isRequested,
        VIRTUAL_KEY aggregateKey,
        VIRTUAL_KEY leftKey,
        VIRTUAL_KEY rightKey)
    {
        if (!isRequested || IsKeyDown(leftKey) || IsKeyDown(rightKey) || aggregateKey != default && IsKeyDown(aggregateKey))
        {
            return;
        }

        inputs[inputCount++] = CreateKeyboardInput(leftKey, false);
        pressedModifiers[pressedModifierCount++] = leftKey;
    }

    private static bool IsKeyDown(VIRTUAL_KEY virtualKey) => (PInvoke.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    private static INPUT CreateKeyboardInput(VIRTUAL_KEY virtualKey, bool isKeyUp) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = isKeyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = InjectedInputMagic,
            },
        },
    };

    private static INPUT CreateMouseInput(int x, int y, MOUSE_EVENT_FLAGS flags) => new()
    {
        type = INPUT_TYPE.INPUT_MOUSE,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            mi = new MOUSEINPUT
            {
                dx = x,
                dy = y,
                dwFlags = flags,
                dwExtraInfo = InjectedInputMagic,
            },
        },
    };

    private static bool HasRootOwner(HWND window, HWND expectedRootWindow) =>
        window != HWND.Null && PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) == expectedRootWindow;

    private static void Send(ReadOnlySpan<INPUT> inputs, string inputKind)
    {
        var sentCount = PInvoke.SendInput(inputs, Unsafe.SizeOf<INPUT>());
        if (sentCount != inputs.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows inserted {sentCount} of {inputs.Length} requested {inputKind} input events.");
        }
    }
}