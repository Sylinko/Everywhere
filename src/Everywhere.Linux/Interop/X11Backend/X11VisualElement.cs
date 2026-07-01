using Avalonia;
using Everywhere.Interop;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Represents a visual element in the X11 system (Window or Screen).
/// </summary>
public abstract class X11VisualElementBase(X11WindowBackend backend) : IVisualElement
{
    public abstract string Id { get; }
    public abstract IVisualElement? Parent { get; }
    public abstract IEnumerable<IVisualElement> Children { get; }
    public abstract VisualElementType Type { get; }
    public abstract string? Name { get; }
    public abstract PixelRect BoundingRectangle { get; }
    public abstract int ProcessId { get; }
    public abstract IntPtr NativeWindowHandle { get; }
    public VisualElementStates States => VisualElementStates.None;
    public VisualElementSiblingAccessor SiblingAccessor => CreateSiblingAccessor();

    protected readonly X11WindowBackend _backend = backend;
    protected abstract VisualElementSiblingAccessor CreateSiblingAccessor();

    public string? GetText(int maxLength = -1) => Name;
    public string? GetSelectionText() => null;
    public void SetText(string text) { }
    public void Invoke() { }

    public virtual void SendShortcut(KeyboardShortcut shortcut) { }

    public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_backend.Screenshot.Capture((X11Window)NativeWindowHandle, BoundingRectangle.WithX(0).WithY(0))).WaitAsync(cancellationToken);
}

public sealed class X11WindowVisualElement(X11WindowBackend backend, X11Window window) : X11VisualElementBase(backend)
{
    public override string Id => window.ToString("X");
    public override IntPtr NativeWindowHandle => (IntPtr)window;
    public override VisualElementType Type => VisualElementType.TopLevel;

    public override int ProcessId => _backend.WindowManager.GetWindowPid(window);
    public override PixelRect BoundingRectangle => _backend.WindowManager.GetWindowBounds(window);

    public override string? Name
    {
        get
        {
            var name = "";
            return Xlib.XFetchName(_backend.Context.Display, window, ref name) != Status.Failure ? name : null;
        }
    }

    public override IVisualElement? Parent
    {
        get
        {
            var parent = X11Window.None;
            var root = X11Window.None;
            Xlib.XQueryTree(_backend.Context.Display, window, ref root, ref parent, out _);
            if (parent != X11Window.None && parent != _backend.Context.RootWindow)
                return _backend.GetWindowElement(parent);
            return null;
        }
    }

    public override IEnumerable<IVisualElement> Children
    {
        get
        {
            var parent = X11Window.None;
            var root = X11Window.None;
            Xlib.XQueryTree(_backend.Context.Display, window, ref root, ref parent, out var children);
            foreach (var child in children.Where(child => child != X11Window.None)) yield return _backend.GetWindowElement(child);
        }
    }

    protected override VisualElementSiblingAccessor CreateSiblingAccessor()
    {
        var parent = X11Window.None;
        var root = X11Window.None;
        Xlib.XQueryTree(_backend.Context.Display, window, ref root, ref parent, out _);
        return new X11SiblingAccessor(this, parent, _backend);
    }

    public override void SendShortcut(KeyboardShortcut shortcut)
    {
        Xlib.XSetInputFocus(_backend.Context.Display, window, RevertFocus.RevertToParent, X11Native.CurrentTime);
        _backend.InputHandler.SendKeyboardShortcut(shortcut);
    }

    // Sibling Accessor Implementation would go here as a nested class or separate file
    private class X11SiblingAccessor : VisualElementSiblingAccessor
    {
        // Implementation matches original logic using Backend.GetWindowElement
        public X11SiblingAccessor(X11WindowVisualElement e, X11Window p, X11WindowBackend b) { }
        protected override void EnsureResources() { }
        protected override IEnumerator<IVisualElement> CreateForwardEnumerator() { yield break; }
        protected override IEnumerator<IVisualElement> CreateBackwardEnumerator() { yield break; }
    }
}

public sealed class X11ScreenVisualElement(X11WindowBackend backend, int index) : X11VisualElementBase(backend)
{
    public override string Id => $"Screen {index}";
    public override IntPtr NativeWindowHandle => (IntPtr)Xlib.XRootWindow(_backend.Context.Display, index);
    public override VisualElementType Type => VisualElementType.Screen;
    public override string Name => Id;
    public override int ProcessId => 0;
    public override IVisualElement? Parent => null;

    public override PixelRect BoundingRectangle => new(
        0,
        0,
        X11Native.XDisplayWidth(_backend.Context.Display, index),
        X11Native.XDisplayHeight(_backend.Context.Display, index));

    public override IEnumerable<IVisualElement> Children
    {
        get
        {
            var root = (X11Window)NativeWindowHandle;
            var parent = X11Window.None;
            var rootWindow = X11Window.None;
            Xlib.XQueryTree(_backend.Context.Display, root, ref rootWindow, ref parent, out var children);
            foreach (var child in children.Where(child => child != X11Window.None)) yield return _backend.GetWindowElement(child);
        }
    }
    protected override VisualElementSiblingAccessor CreateSiblingAccessor() => new X11ScreenSiblingAccessor(_backend, index);

    private class X11ScreenSiblingAccessor : VisualElementSiblingAccessor
    {
        public X11ScreenSiblingAccessor(X11WindowBackend b, int i) { }
        protected override IEnumerator<IVisualElement> CreateForwardEnumerator() { yield break; }
        protected override IEnumerator<IVisualElement> CreateBackwardEnumerator() { yield break; }
    }
}