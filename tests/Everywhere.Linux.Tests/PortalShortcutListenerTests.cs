using System.Reflection;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Linux.Tests;

public sealed class PortalShortcutListenerTests
{
    [TestCase(Key.E, KeyModifiers.Control | KeyModifiers.Shift, "CTRL+SHIFT+e")]
    [TestCase(Key.W, KeyModifiers.Meta, "LOGO+w")]
    [TestCase(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Alt, "CTRL+ALT+equal")]
    [TestCase(Key.NumPad7, KeyModifiers.Control, "CTRL+KP_7")]
    [TestCase(Key.PageDown, KeyModifiers.Control, "CTRL+Next")]
    public void KeyboardChord_FormatForPortal_UsesBaseLayerXkbNames(Key key, KeyModifiers modifiers, string expected)
    {
        var type = typeof(PortalShortcutListener).Assembly.GetType("Everywhere.Linux.Desktop.PortalShortcutSession")
            ?? throw new TypeLoadException("PortalShortcutSession");
        var format = type.GetMethod("FormatTrigger") ?? throw new MissingMethodException(type.FullName, "FormatTrigger");
        Assert.That(format.Invoke(null, [new KeyboardShortcut(key, modifiers)]), Is.EqualTo(expected));
    }

    [Test]
    public async Task SharedRegistration_DisposeOneHandler_KeepsOtherCallbacksAndSuspendsDuringRecording()
    {
        await using var listener = new PortalShortcutListener(NullLogger<PortalShortcutListener>.Instance);
        var shortcut = new KeyboardShortcut(Key.E, KeyModifiers.Control | KeyModifiers.Shift);
        var firstCalls = 0;
        var secondCalls = 0;
        using var first = listener.Register(shortcut, () => firstCalls++);
        using var second = listener.Register(shortcut, () => secondCalls++);

        Activate(listener, shortcut);
        first.Dispose();
        Activate(listener, shortcut);
        using (var recording = listener.StartCaptureKeyboardShortcut())
        {
            Assert.That(listener.StartCaptureKeyboardShortcut(), Is.SameAs(recording));
            Activate(listener, shortcut);
        }
        Activate(listener, shortcut);
        second.Dispose();
        Activate(listener, shortcut);

        Assert.Multiple(() =>
        {
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Listener_Dispose_DiscardsQueuedActivationAndRejectsNewRegistrations()
    {
        var listener = new PortalShortcutListener(NullLogger<PortalShortcutListener>.Instance);
        var shortcut = new KeyboardShortcut(Key.E, KeyModifiers.Control | KeyModifiers.Shift);
        var calls = 0;
        using var registration = listener.Register(shortcut, () => calls++);
        await listener.DisposeAsync();
        Activate(listener, shortcut);
        Assert.That(calls, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => listener.Register(shortcut, () => calls++));
    }

    private static void Activate(PortalShortcutListener listener, KeyboardShortcut shortcut)
    {
        var method = typeof(PortalShortcutListener).GetMethod("HandleActivated", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(PortalShortcutListener), "HandleActivated");
        method.Invoke(listener, [$"keyboard_{(int)shortcut.Modifiers}_{(int)shortcut.Key}"]);
    }
}