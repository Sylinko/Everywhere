#if LINUX
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Views;

[NonParallelizable]
public sealed class KeyboardShortcutInputBoxTests
{
    [AvaloniaTest]
    public void ModifierChords_Record_CommitOnlyAfterKeyRelease()
    {
        var combinations = new[]
        {
            (Key.LeftCtrl, KeyModifiers.Control, RawInputModifiers.Control),
            (Key.RightAlt, KeyModifiers.Alt, RawInputModifiers.Alt),
            (Key.LeftShift, KeyModifiers.Shift, RawInputModifiers.Shift),
            (Key.LWin, KeyModifiers.Meta, RawInputModifiers.Meta),
        };
        foreach (var (key, modifiers, rawModifiers) in combinations)
        {
            using var target = new Target();
            target.Control.EnterRecordingModeCommand.Execute(null);
            target.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(target.Control.DisplayShortcut.Modifiers, Is.EqualTo(modifiers));
            Assert.That(target.Control.Shortcut, Is.EqualTo(Target.Original));
            target.Window.KeyPress(Key.K, rawModifiers, PhysicalKey.None, null);
            Assert.That(target.Control.Shortcut, Is.EqualTo(Target.Original));
            target.Window.KeyRelease(Key.K, rawModifiers, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(target.Control.Shortcut, Is.EqualTo(new KeyboardShortcut(Key.K, modifiers)));
                Assert.That(target.Control.IsRecording, Is.False);
                Assert.That(target.Listener.Scope.IsDisposed, Is.True);
            });
        }
    }

    [AvaloniaTest]
    public void PartialChord_EscapeOrFocusLoss_RestoresOriginalShortcut()
    {
        using var target = new Target();
        target.Control.EnterRecordingModeCommand.Execute(null);
        target.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.None, null);
        target.Window.KeyPress(Key.Escape, RawInputModifiers.Control, PhysicalKey.None, null);
        Assert.That(target.Control.Shortcut, Is.EqualTo(Target.Original));
        Assert.That(target.Control.IsRecording, Is.False);

        target.Control.EnterRecordingModeCommand.Execute(null);
        target.Window.KeyPress(Key.K, RawInputModifiers.Alt, PhysicalKey.None, null);
        target.Other.Focus();
        Assert.That(target.Control.Shortcut, Is.EqualTo(Target.Original));
        Assert.That(target.Listener.Scope.IsDisposed, Is.True);
    }

    [AvaloniaTest]
    public void ModifierReleasedBeforeKey_Finish_PreservesTheRecordedChord()
    {
        using var target = new Target();
        target.Control.EnterRecordingModeCommand.Execute(null);
        target.Window.KeyPress(Key.K, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        target.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.Shift, PhysicalKey.None, null);
        target.Window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.None, null);
        target.Window.KeyRelease(Key.K, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(target.Control.Shortcut, Is.EqualTo(new KeyboardShortcut(Key.K, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    [AvaloniaTest]
    public void ActiveRecording_Detach_ReleasesLeaseAndPreservesSetting()
    {
        using var target = new Target();
        target.Control.EnterRecordingModeCommand.Execute(null);
        target.Window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.None, null);
        target.Panel.Children.Remove(target.Control);
        Assert.Multiple(() =>
        {
            Assert.That(target.Control.Shortcut, Is.EqualTo(Target.Original));
            Assert.That(target.Control.IsRecording, Is.False);
            Assert.That(target.Listener.Scope.IsDisposed, Is.True);
        });
    }

    [AvaloniaTest]
    public void HostCancellation_WhileRecording_PreservesSettingAndAllowsAnotherLease()
    {
        using var target = new Target();
        target.Control.EnterRecordingModeCommand.Execute(null);
        target.Listener.Scope.Cancel();
        Assert.That(target.Control.Shortcut, Is.EqualTo(Target.Original));
        Assert.That(target.Control.IsRecording, Is.False);
        target.Control.EnterRecordingModeCommand.Execute(null);
        target.Window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.None, null);
        target.Window.KeyRelease(Key.P, RawInputModifiers.Control, PhysicalKey.None, null);
        Assert.That(target.Control.Shortcut, Is.EqualTo(new KeyboardShortcut(Key.P, KeyModifiers.Control)));
    }

    private sealed class Target : IDisposable
    {
        public static KeyboardShortcut Original { get; } = new(Key.E, KeyModifiers.Control | KeyModifiers.Shift);
        public KeyboardShortcutInputBox Control { get; } = new()
        {
            Focusable = true,
            Shortcut = Original,
        };
        public Button Other { get; } = new();
        public StackPanel Panel { get; } = new();
        public Window Window { get; }
        public Listener Listener { get; } = new();

        private readonly FieldInfo _locator = typeof(ServiceLocator).GetField("_serviceProvider", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ServiceLocator));
        private readonly object? _previousProvider;
        private readonly ServiceProvider _provider;

        public Target()
        {
            _previousProvider = _locator.GetValue(null);
            _provider = new ServiceCollection().AddSingleton<IShortcutListener>(Listener).BuildServiceProvider();
            _locator.SetValue(null, _provider);
            Panel.Children.Add(Control);
            Panel.Children.Add(Other);
            Window = new Window
            {
                Content = Panel,
            };
            Window.Show();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Window.Close();
            _locator.SetValue(null, _previousProvider);
            _provider.Dispose();
        }
    }

    private sealed class Listener : IShortcutListener
    {
        public Scope Scope { get; private set; } = new();

        /// <inheritdoc />
        public IDisposable Register(KeyboardShortcut shortcut, Action handler) => throw new NotSupportedException();

        /// <inheritdoc />
        public IDisposable Register(MouseShortcut shortcut, Action handler) => throw new NotSupportedException();

        /// <inheritdoc />
        public IKeyboardShortcutScope StartCaptureKeyboardShortcut() => Scope = new Scope();
    }

    private sealed class Scope : IKeyboardShortcutScope
    {
        /// <inheritdoc />
        public KeyboardShortcut PressingShortcut => default;

        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        /// <inheritdoc />
        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        public void Cancel() => ShortcutFinished?.Invoke(this, default);

        /// <inheritdoc />
        public void Dispose() => IsDisposed = true;
    }
}
#endif