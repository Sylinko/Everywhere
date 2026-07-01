using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Interop;
using GObj = GObject.Object;
using GObjHandle = GObject.Internal.ObjectHandle;
using Microsoft.Extensions.Logging;

namespace Everywhere.Linux.Interop;

/// <summary>
/// AT-SPI（Assistive Technology Service Provider Interface）
/// see doc: https://gnome.pages.gitlab.gnome.org/at-spi2-core/libatspi/index.html
/// </summary>
public sealed partial class AtspiService
{
    private readonly bool _initialized;
    private readonly IWindowBackend _windowBackend;
    private readonly ConcurrentDictionary<GObj, AtspiVisualElement> _cachedElement = new();
    private readonly ILogger<AtspiService> _logger = ServiceLocator.Resolve<ILogger<AtspiService>>();
    private readonly AtspiEventListenerCallback _eventCallback;
    private readonly Lock _focusLock = new();
    private readonly GObj _root;

    private IntPtr _eventListener;
    private GObj? _focusedElement;

    public AtspiService(IWindowBackend backend)
    {
        _windowBackend = backend;
        GObject.Module.Initialize();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AT_SPI_BUS")))
            Environment.SetEnvironmentVariable("AT_SPI_BUS", "session");

        if (atspi_init() < 0)
            throw new InvalidOperationException("Failed to initialize AT-SPI");
        _eventCallback = OnEvent;
        _eventListener = atspi_event_listener_new(_eventCallback, IntPtr.Zero, IntPtr.Zero);
        atspi_event_listener_register(
            _eventListener,
            Marshal.StringToCoTaskMemUTF8("object:state-changed:focused"),
            IntPtr.Zero);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            atspi_event_main();
        });
        _root = GObjWrapper.Wrap(atspi_get_desktop(0));
        _initialized = true;
    }

    private void CheckInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("At-SPI not initialized");
        }
    }

    ~AtspiService()
    {
        if (_eventListener != IntPtr.Zero)
        {
            atspi_event_listener_deregister(
                _eventListener,
                Marshal.StringToCoTaskMemUTF8("object:state-changed:focused"),
                IntPtr.Zero);
            _eventListener = IntPtr.Zero;
        }
        atspi_exit();
    }

    private void OnEvent(IntPtr atspiEventPtr, IntPtr userData)
    {
        try
        {
            var ev = Marshal.PtrToStructure<AtspiEvent>(atspiEventPtr);
            var eventType = Marshal.PtrToStringAnsi(ev.type) ?? string.Empty;
            if (eventType.Contains("focused") && ev.source != IntPtr.Zero)
            {
                lock (_focusLock)
                {
                    if (ev.detail1 == 1) // focus in
                    {
                        var element = ev.source;
                        // var appName = Process.GetProcessById(ElementPid(element)).ProcessName;
                        // _logger.LogDebug("Focus in: {app} - {Name}", appName, ElementName(element));
                        _focusedElement = GObjWrapper.Wrap(element);
                    }
                    else
                    {
                        _focusedElement = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnEvent failed: {Message}", ex.Message);
        }
    }

    private static string? ElementName(GObj elem)
    {
        try
        {
            return atspi_accessible_get_name(elem.Handle, IntPtr.Zero);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static int ElementPid(GObj elem)
    {
        var pid = atspi_accessible_get_process_id(elem.Handle, IntPtr.Zero);
        return pid;
    }

    private static PixelRect ElementBounds(GObj elem)
    {

        var rectPtr = atspi_component_get_extents(elem.Handle, (int)AtspiCoordType.Screen, IntPtr.Zero);
        var rect = Marshal.PtrToStructure<AtspiRect>(rectPtr);
        GLib.Functions.Free(rectPtr);
        return new PixelRect(rect.x, rect.y, rect.width, rect.height);
    }

    private static VisualElementStates ElementState(GObj elem)
    {
        try
        {
            var states = VisualElementStates.None;
            using (var elemStateset = GObjWrapper.Wrap(atspi_accessible_get_state_set(elem.Handle)))
            {
                if ((atspi_state_set_contains(elemStateset.Handle, (int)AtspiState.Visible) == 0)
                    || (atspi_state_set_contains(elemStateset.Handle, (int)AtspiState.Showing) == 0))
                    states |= VisualElementStates.Offscreen;
                if (atspi_state_set_contains(elemStateset.Handle, (int)AtspiState.Enable) == 0) states |= VisualElementStates.Disabled;
                if (atspi_state_set_contains(elemStateset.Handle, (int)AtspiState.Focused) == 1) states |= VisualElementStates.Focused;
                if (atspi_state_set_contains(elemStateset.Handle, (int)AtspiState.Selected) == 1) states |= VisualElementStates.Selected;
                if (atspi_state_set_contains(elemStateset.Handle, (int)AtspiState.Editable) == 0) states |= VisualElementStates.ReadOnly;
            }

            if (atspi_accessible_get_role(elem.Handle, IntPtr.Zero) == (int)AtspiRole.PasswordText)
                states |= VisualElementStates.Password;
            return states;
        }
        catch (COMException)
        {
            return VisualElementStates.None;
        }
    }

    private GObj? TryMatchRelationWindow(GObj window, bool down)
    {
        var rawSet = atspi_accessible_get_relation_set(window.Handle, IntPtr.Zero);
        if (rawSet == IntPtr.Zero) return null;
        var array = new GObjArray(rawSet);
        if (array.Length == 0)
        {
            return null;
        }

        foreach (var relation in array.Iterate())
        {
            var type = atspi_relation_get_relation_type(relation.Handle);
            var nTarget = atspi_relation_get_n_targets(relation.Handle);
            if (((type != (int)AtspiRelationType.Embeds && (type != (int)AtspiRelationType.SubwindowOf || !down))
                    && (type != (int)AtspiRelationType.EmbeddedBy || (down)))
                || nTarget < 1) continue;
            var target = GObjWrapper.Wrap(atspi_relation_get_target(relation.Handle, 0));
            if (ElementVisible(target))
            {
                return target;
            }
        }
        return null;
    }

    private bool ElementVisible(GObj elem, bool includeApp = false)
    {
        if (ElementState(elem).HasFlag(VisualElementStates.Offscreen)) return false;
        if (includeApp && atspi_accessible_is_application(elem.Handle) != 0)
        {
            return true;
        }
        if (atspi_accessible_is_component(elem.Handle) == 0) return false;
        var rect = ElementBounds(elem);
        return rect is { Height: > 0, Width: > 0 };
    }

    private IEnumerable<AtspiVisualElement> ElementChildren(GObj elem)
    {
        var relatedSubWindow = TryMatchRelationWindow(elem, true);
        if (relatedSubWindow != null && ElementVisible(relatedSubWindow, true))
        {
            var sub = GetAtspiVisualElement(() => relatedSubWindow);
            if (sub != null)
            {
                yield return sub;
            }
        }
        else
        {
            var count = atspi_accessible_get_child_count(elem.Handle, IntPtr.Zero);
            var i = 0;
            while (i < count)
            {
                var child = GObjWrapper.WrapAllowNull(atspi_accessible_get_child_at_index(elem.Handle, i, IntPtr.Zero));
                if (child != null && ElementVisible(child, true))
                {
                    var childElem = GetAtspiVisualElement(() => child);
                    if (childElem != null)
                    {
                        yield return childElem;
                    }
                }
                i++;
            }
        }
    }

    private AtspiVisualElement? AtspiElementFromPoint(AtspiVisualElement? parent, PixelPoint point, out int depth, bool root = false)
    {

        parent ??= GetAtspiVisualElement(() => _root);
        depth = 0;
        if (parent is null)
        {
            return null;
        }
        // _logger.LogDebug("find: {Name}, {Rect}, {Visible}", parent.Name, parent.BoundingRectangle, ElementVisible(parent._element));
        if (!root && !ElementVisible(parent._element, true))
        {
            return null;
        }
        var rect = parent.BoundingRectangle;
        if (rect is { Height: > 0, Width: > 0 } && !rect.Contains(point))
        {
            return null;
        }
        var maxDepth = -1;
        AtspiVisualElement? foundChild = null;
        foreach (var child in ElementChildren(parent._element)
                     .OrderByDescending(child => child.Order))
        {
            var found = AtspiElementFromPoint(child, point, out var subdepth);
            if (found != null && subdepth > maxDepth)
            {
                maxDepth = subdepth;
                foundChild = found;
            }
        }
        if (foundChild != null)
        {
            depth = maxDepth + 1;
            return foundChild;
        }
        if (rect is { Height: > 0, Width: > 0 } && rect.Contains(point) && ElementVisible(parent._element))
        {
            return parent;
        }

        return null;
    }

    private AtspiVisualElement? AtspiAppElementByPid(int pid)
    {
        var root = GetAtspiVisualElement(() => _root);
        if (root is null)
        {
            return null;
        }
        foreach (var child in ElementChildren(root._element)
                     .OrderByDescending(child => child.Order))
        {
            if (child.ProcessId == pid)
            {
                return child;
            }
        }
        return null;
    }

    public IVisualElement? ElementFromWindow(PixelPoint point, IVisualElement window)
    {
        CheckInitialized();
        var app = AtspiAppElementByPid(window.ProcessId);
        if (app == null)
        {
#if DEBUG
            _logger.LogDebug("App {ProcessId} do not support At-SPI", window.ProcessId);
#endif
            return null;
        }
        var elem = AtspiElementFromPoint(app, point, out _, true);
#if DEBUG
        if (elem == null)
        {
            _logger.LogDebug("AtspiElementFromPoint {Point} not found", point);
        }
        else
        {
            _logger.LogDebug(
                "AtspiElementFromPoint {Point} found: {Name}, {Rect}",
                point,
                elem.Name,
                elem.BoundingRectangle);
        }
#endif
        return elem;
    }

    public IVisualElement? ElementFocused()
    {
        try
        {
            CheckInitialized();
            lock (_focusLock)
            {
                return GetAtspiVisualElement(() => _focusedElement);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AtspiFocusedElement failed");
            return null;
        }
    }

    private class AtspiVisualElement(AtspiService atspi, GObj element)
        : IVisualElement
    {
        public readonly GObj _element = element;
        private readonly List<GObj> _cachedAccessibleChildren = [];
        private bool _childrenCached;
        private readonly Lock _childrenLoading = new();


        public IVisualElement? Parent
        {
            get
            {
                var parent = atspi.TryMatchRelationWindow(_element, false) ??
                    GObjWrapper.WrapAllowNull(atspi_accessible_get_parent(_element.Handle, IntPtr.Zero));
                return parent != null && atspi_accessible_is_application(parent.Handle) != 0 ?
                    null :
                    atspi.GetAtspiVisualElement(() => parent);
            }
        }

        private int IndexInParent => atspi_accessible_get_index_in_parent(_element.Handle, IntPtr.Zero);

        private void EnsureChildCached()
        {
            lock (_childrenLoading)
            {
                if (!_childrenCached)
                {
                    _cachedAccessibleChildren.Clear();
                    foreach (var elem in atspi.ElementChildren(_element))
                    {
                        _cachedAccessibleChildren.Add(elem._element);
                    }
                    _childrenCached = true;
                }
            }
        }

        public IEnumerable<IVisualElement> Children
        {
            get
            {
                EnsureChildCached();
                foreach (var child in _cachedAccessibleChildren)
                {
                    yield return atspi._cachedElement[child];
                }
            }
        }

        private class AtspiSiblingAccessor(
            AtspiService atspi,
            AtspiVisualElement? parent,
            AtspiVisualElement element
        ) : VisualElementSiblingAccessor
        {
            private int _index;

            protected override void EnsureResources()
            {
                base.EnsureResources();
                parent?.EnsureChildCached();
                if (parent == null) return;
                var index = 0;
                foreach (var child in parent._cachedAccessibleChildren)
                {
                    if (child == element._element)
                    {
                        _index = index;
                    }
                    index++;
                }
            }

            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                if (parent == null)
                {
                    yield break;
                }
                for (var i = _index + 1; i < parent._cachedAccessibleChildren.Count; i++)
                {
                    yield return atspi._cachedElement[parent._cachedAccessibleChildren[i]];
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                if (parent == null)
                {
                    yield break;
                }
                for (var i = _index - 1; i >= 0; i--)
                {
                    yield return atspi._cachedElement[parent._cachedAccessibleChildren[i]];
                }
            }
        }

        public VisualElementSiblingAccessor SiblingAccessor => new AtspiSiblingAccessor(atspi, (AtspiVisualElement?)Parent, this);

        public VisualElementType Type
        {
            get
            {
                try
                {
                    // Atspi role Application stands for App Process,
                    // however it does not ensure impl Component(stands for UI), making the root element no width and height;
                    // Atspi role Frame stands for "A top level window with a title bar, border, menubar, etc."(doc says)
                    // Window: A top level window with no title or border.
                    // But this actually exists: App > Frame > Frame > etc.
                    // So check TopLevel first as the top-most Frame/Window
                    // Sub Frame recognized as Panel
                    var roleEnum = (AtspiRole)atspi_accessible_get_role(_element.Handle, IntPtr.Zero);
                    var rawParent = GObjWrapper.WrapAllowNull(atspi_accessible_get_parent(_element.Handle, IntPtr.Zero));
                    if ((roleEnum is AtspiRole.Frame or AtspiRole.Window) &&
                        rawParent != null && atspi_accessible_is_application(rawParent.Handle) != 0)
                    {
                        return VisualElementType.TopLevel;
                    }
                    return roleEnum switch
                    {
                        AtspiRole.Application => VisualElementType.TopLevel,
                        AtspiRole.Button or AtspiRole.ToggleButton or AtspiRole.PushButton => VisualElementType.Button,
                        AtspiRole.CheckBox or AtspiRole.Switch => VisualElementType.CheckBox,
                        AtspiRole.ComboBox => VisualElementType.ComboBox,
                        AtspiRole.DocumentEmail or AtspiRole.DocumentFrame or AtspiRole.DocumentPresentation or AtspiRole.DocumentSpreadsheet or
                            AtspiRole.DocumentText or AtspiRole.DocumentWeb or AtspiRole.HtmlContainer or
                            AtspiRole.Article => VisualElementType.Document,
                        AtspiRole.Entry or AtspiRole.Editbar or AtspiRole.PasswordText => VisualElementType.TextEdit,
                        AtspiRole.Image or AtspiRole.DesktopIcon or AtspiRole.Icon => VisualElementType.Image,
                        AtspiRole.Label or AtspiRole.Text or AtspiRole.Footer or AtspiRole.Caption or AtspiRole.Comment or
                            AtspiRole.DescriptionTerm or AtspiRole.Footnote or AtspiRole.Paragraph or AtspiRole.DescriptionValue
                            => VisualElementType.Label,
                        AtspiRole.Header => VisualElementType.Header,
                        AtspiRole.Link => VisualElementType.Hyperlink,
                        AtspiRole.List or AtspiRole.ListBox or AtspiRole.DescriptionList => VisualElementType.ListView,
                        AtspiRole.ListItem => VisualElementType.ListViewItem,
                        AtspiRole.Menu or AtspiRole.LandMark => VisualElementType.Menu,
                        AtspiRole.MenuItem or AtspiRole.CheckMenuItem or AtspiRole.TearoffMenuItem => VisualElementType.MenuItem,
                        AtspiRole.PageTabList => VisualElementType.TabControl,
                        AtspiRole.PageTab => VisualElementType.TabItem,
                        AtspiRole.Panel or AtspiRole.ScrollPane or AtspiRole.RootPane or AtspiRole.Canvas or AtspiRole.Frame or AtspiRole.Window
                            or AtspiRole.Section => VisualElementType.Panel,
                        AtspiRole.ProgressBar => VisualElementType.ProgressBar,
                        AtspiRole.RadioButton => VisualElementType.RadioButton,
                        AtspiRole.ScrollBar => VisualElementType.ScrollBar,
                        AtspiRole.SpinButton => VisualElementType.Spinner,
                        AtspiRole.SplitPane => VisualElementType.Splitter,
                        AtspiRole.StatusBar => VisualElementType.StatusBar,
                        AtspiRole.Slider => VisualElementType.Slider,
                        AtspiRole.Table or AtspiRole.Form => VisualElementType.Table,
                        AtspiRole.TableRow => VisualElementType.TableRow,
                        AtspiRole.ToolBar => VisualElementType.ToolBar,
                        AtspiRole.Tree => VisualElementType.TreeViewItem,
                        AtspiRole.TreeTable => VisualElementType.TreeView,
                        _ => VisualElementType.Unknown
                    };
                }
                catch (COMException)
                {
                    return VisualElementType.Unknown;
                }
            }
        }

        public VisualElementStates States => ElementState(_element);

        public string? Name => ElementName(_element);

        public string Id
        {
            get
            {
                var idStr = atspi_accessible_get_accessible_id(_element.Handle, IntPtr.Zero);
                return idStr.IsNullOrEmpty() ? _element.GetHashCode().ToString("X") : idStr;
            }
        }

        public int ProcessId => ElementPid(_element);

        private IVisualElement? OwnerWindow
        {
            get
            {
                if (field != null) return field;
                // note: one process may open several root windows, only pid is not enough
                // for not matched element, match its parent continuely.
                // window level is always coresponded with that in at-spi
                var owner = atspi._windowBackend.GetWindowElementByInfo(ProcessId, BoundingRectangle)
                    ?? ((AtspiVisualElement?)Parent)?.OwnerWindow;
                field = owner;
                return field;
            }
        }

        public nint NativeWindowHandle => OwnerWindow?.NativeWindowHandle ?? IntPtr.Zero;

        public string? GetText(int maxLength = -1)
        {
            if (atspi_accessible_is_text(_element.Handle) == 0) return null;
            var objTextCount = atspi_accessible_get_child_count(_element.Handle, IntPtr.Zero);
            if (objTextCount > 0)
            {
                // in this case, libatspi return objTextCount char “obj char” (U+FFFC), we just simply return null
                return null;
            }
            var count = atspi_text_get_character_count(_element.Handle, IntPtr.Zero);
            return atspi_text_get_text(_element.Handle, 0, maxLength == -1 ? count : maxLength, IntPtr.Zero);
        }

        public string? GetSelectionText()
        {
            if (atspi_accessible_is_text(_element.Handle) == 0) return null;
            var nSelections = atspi_text_get_n_selections(_element.Handle, IntPtr.Zero);
            var selected = new StringBuilder();
            for (var i = 0; i < nSelections; i++)
            {
                using (var rawRange = GObjWrapper.Wrap(atspi_text_get_selection(_element.Handle, i, IntPtr.Zero)))
                {
                    var range = Marshal.PtrToStructure<AtspiRange>(rawRange.Handle.DangerousGetHandle());
                    var text = atspi_text_get_text(_element.Handle, range.start, range.end, IntPtr.Zero);
                    if (!text.IsNullOrEmpty())
                    {
                        selected.Append(text);
                    }
                }
            }
            return selected.Length > 0 ? selected.ToString() : null;
        }

        public void Invoke()
        {
            if (atspi_accessible_is_action(_element.Handle) == 0)
            {
                return;
            }
            var nAction = atspi_action_get_n_actions(_element.Handle, IntPtr.Zero);
            if (nAction == 0)
            {
                return;
            }
            atspi_action_do_action(_element.Handle, 0, IntPtr.Zero);
        }

        public void SetText(string text)
        {
            if (States.HasFlag(VisualElementStates.ReadOnly))
            {
                return;
            }
            if (atspi_accessible_is_editable_text(_element.Handle) == 0)
            {
                return;
            }
            atspi_editable_text_set_text_contents(_element.Handle, Marshal.StringToCoTaskMemUTF8(text), IntPtr.Zero);
        }

        public void SendShortcut(KeyboardShortcut shortcut)
        {
            if (atspi_accessible_is_component(_element.Handle) == 0)
            {
                return;
            }
            if (atspi_component_grab_focus(_element.Handle, IntPtr.Zero) == 0)
            {
                return;
            }
            atspi._windowBackend.SendKeyboardShortcut(shortcut);
        }

        private static int LayerOrder(AtspiLayer layer)
        {
            return layer switch
            {
                AtspiLayer.Background => 1,
                AtspiLayer.Window => 2,
                AtspiLayer.Mdi => 3,
                AtspiLayer.Canvas => 4,
                AtspiLayer.Widget => 5,
                AtspiLayer.Popup => 6,
                AtspiLayer.Overlay => 7,
                _ => 0
            };
        }

        public int Order
        {
            get
            {
                var layer = atspi_component_get_layer(_element.Handle, IntPtr.Zero);
                var z = atspi_component_get_mdi_z_order(_element.Handle, IntPtr.Zero);
                return LayerOrder((AtspiLayer)layer) * 256 + z * 16 - IndexInParent;
            }
        }

        public PixelRect BoundingRectangle
        {
            get
            {
                if (atspi_accessible_is_component(_element.Handle) == 0)
                {
                    var unionRect = new PixelRect();
                    foreach (var child in atspi.ElementChildren(_element))
                    {
                        unionRect.Union(child.BoundingRectangle);
                    }
                    return unionRect;
                }
                var rect = ElementBounds(_element);
                // atspi._logger.LogDebug(
                //     "Element {Name} BoundingRectangle: {X},{Y} - {W}x{H}",
                //     Name,
                //     rect.X,
                //     rect.Y,
                //     rect.Width,
                //     rect.Height);
                return rect;
            }
        }

        public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
        {
            var rect = BoundingRectangle;
            if (OwnerWindow != null)
            {
                rect = rect.Translate(-(PixelVector)OwnerWindow.BoundingRectangle.Position);
            }

            return Task.FromResult(atspi._windowBackend.Capture(this, rect));
        }
    }

    private AtspiVisualElement? GetAtspiVisualElement(Func<GObj?> provider)
    {
        try
        {
            var elementObj = provider();
            if (elementObj == null)
            {
                return null;
            }
            if (_cachedElement.TryGetValue(elementObj, out var visualElement)) return visualElement;
            var elem = new AtspiVisualElement(this, elementObj);
            // _logger.LogDebug("Element add: {Name}({Type})[{States}]", elem.Name, elem.Type, elem.States);
            _cachedElement[elementObj] = elem;
            return elem;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static class GObjWrapper
    {
        public static T Wrap<T>(IntPtr handle, bool owned) where T : GObj
        {
            return (T)GObject.Internal.InstanceWrapper.WrapHandle<T>(handle, owned);
        }

        public static GObj? WrapAllowNull(IntPtr handle, bool owned = true)
        {
            return handle == IntPtr.Zero ? null : Wrap<GObj>(handle, owned);
        }

        public static GObj Wrap(IntPtr handle, bool owned = true)
        {
            return Wrap<GObj>(handle, owned);
        }
    }

    private class GObjArray : IDisposable
    {
        private IntPtr _handle;
        private readonly NativeGArray _struct;
        private bool _disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeGArray
        {
            public IntPtr Data;
            public uint Len;
        }

        public GObjArray(IntPtr handle)
        {
            _handle = handle;
            if (_handle != IntPtr.Zero)
            {
                _struct = Marshal.PtrToStructure<NativeGArray>(_handle);
            }
        }

        public uint Length => _struct.Len;

        /// <summary>
        /// Iterate GArray data as <see cref="GObj" /> instances.
        /// </summary>
        public IEnumerable<GObj> Iterate()
        {
            if (_handle == IntPtr.Zero || _struct.Data == IntPtr.Zero)
                yield break;

            for (var i = 0; i < Length; i++)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(GObjArray));
                var ptr = Marshal.ReadIntPtr(_struct.Data, i * IntPtr.Size);
                if (ptr == IntPtr.Zero) continue;
                yield return GObjWrapper.Wrap<GObj>(ptr, true);
            }
        }

        public void Dispose()
        {
            if (_disposed || _handle == IntPtr.Zero) return;
            GLib.Internal.Functions.Free(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }

    private const string LibAtspi = "libatspi.so.0";

    private enum AtspiCoordType
    {
        Screen = 0,
        Window = 1,
        Parent = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtspiRect
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    /// <summary>
    /// Role
    /// </summary>
    private enum AtspiRole
    {
        Invalid = 0,
        // Label(Text)
        Label = 29,
        Text = 61,
        Header = 71,
        Footer = 72,
        Caption = 81,
        Comment = 97,
        DescriptionTerm = 122,
        Footnote = 124,
        Paragraph = 73,
        DescriptionValue = 123,
        // Button
        Button = 43,
        ToggleButton = 62,
        PushButton = 129,
        // TextEdit
        Entry = 79, // ATSPI_STATE_EDITABLE else Text Role
        Editbar = 77,
        PasswordText = 40,
        // Document
        Article = 109,
        DocumentFrame = 82,
        DocumentSpreadsheet = 92,
        DocumentPresentation = 93,
        DocumentText = 94,
        DocumentWeb = 95,
        DocumentEmail = 96,
        HtmlContainer = 25,
        Form = 87,
        // Hyperlink
        Link = 88,
        // Image
        Image = 27,
        DesktopIcon = 13,
        Icon = 26,
        // CheckBox
        CheckBox = 7,
        Switch = 130,
        // RadioButton
        RadioButton = 44,
        // ComboBox
        ComboBox = 11,
        // ListView
        List = 31,
        ListBox = 98,
        DescriptionList = 121,
        // ListViewItem
        ListItem = 32,
        // TreeView
        TreeTable = 66,
        // TreeViewItem
        Tree = 65,
        // Tab
        PageTabList = 38,
        PageTab = 37,
        // Table
        Table = 55,
        // TableRow
        TableRow = 90,
        // Menu
        Menu = 33,
        LandMark = 110,
        // MenuItem
        MenuItem = 35,
        CheckMenuItem = 8,
        TearoffMenuItem = 59,
        // Slider
        Slider = 51,
        // ScrollBar
        ScrollBar = 48,
        // StatusBar
        StatusBar = 54,
        // ToolBar
        ToolBar = 63,
        // ProgressBar
        ProgressBar = 42,
        // Spinner
        SpinButton = 52,
        // Splitter
        SplitPane = 53,
        // Panel
        ScrollPane = 49,
        RootPane = 46,
        Panel = 39,
        Canvas = 6,
        Section = 85,
        // TopLevel
        Frame = 23,
        Window = 69,
        Application = 75,
    }

    /// <summary>
    /// States
    /// </summary>
    private enum AtspiState
    {
        // Offscreen
        Showing = 25,
        Visible = 30,
        // Disabled
        Enable = 8,
        // Focused
        Focused = 12,
        // Selected
        Selected = 23,
        // ReadOnly
        Editable = 7
    }
    // Password
    // refer to Role

    /// <summary>
    /// Relation Type
    /// </summary>
    private enum AtspiRelationType
    {
        SubwindowOf = 12,
        Embeds = 13,
        EmbeddedBy = 14
    }

    /// <summary>
    /// Component Layer
    /// </summary>
    private enum AtspiLayer
    {
        Invalid = 0,
        Background = 1,
        Canvas = 2,
        Widget = 3,
        Mdi = 4,
        Popup = 5,
        Overlay = 6,
        Window = 7
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtspiEvent
    {
        public IntPtr type; // char*
        public IntPtr source; // AtspiAccessible*
        public int detail1;
        public int detail2;
        // GValue 
        public IntPtr anyValueType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] anyValueData;
        public IntPtr sender;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtspiRange
    {
        public int start;
        public int end;
    }

    public delegate void AtspiEventListenerCallback(IntPtr atspiEvent, IntPtr userData);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_init();

    [LibraryImport(LibAtspi)]
    public static partial void atspi_exit();

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_event_listener_new(AtspiEventListenerCallback callbackEvent, IntPtr userData, IntPtr callbackDestroyed);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_event_listener_register(IntPtr listener, IntPtr eventTypeChar, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_event_listener_deregister(IntPtr listener, IntPtr eventTypeChar, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial void atspi_event_main();

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_get_desktop(int i);


    [LibraryImport(LibAtspi, StringMarshalling = StringMarshalling.Utf8)]
    public static partial string atspi_accessible_get_name(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_role(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_state_set(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_state_set_contains(GObjHandle set, int state);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_parent(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_child_count(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_child_at_index(GObjHandle accessible, int index, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_index_in_parent(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_get_process_id(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi, StringMarshalling = StringMarshalling.Utf8)]
    public static partial string atspi_accessible_get_accessible_id(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_accessible_get_relation_set(GObjHandle accessible, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_relation_get_n_targets(GObjHandle relation);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_relation_get_relation_type(GObjHandle relation);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_relation_get_target(GObjHandle accessible, int i);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_application(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_component(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_component_get_layer(GObjHandle component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_component_get_extents(GObjHandle component, int coordType, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial short atspi_component_get_mdi_z_order(GObjHandle component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_component_grab_focus(GObjHandle component, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_text(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_text_get_character_count(GObjHandle text, IntPtr error);

    [LibraryImport(LibAtspi, StringMarshalling = StringMarshalling.Utf8)]
    public static partial string atspi_text_get_text(GObjHandle text, int start, int end, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_text_get_n_selections(GObjHandle text, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial IntPtr atspi_text_get_selection(GObjHandle text, int selectionNum, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_editable_text(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_editable_text_set_text_contents(GObjHandle editable, IntPtr text, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_accessible_is_action(GObjHandle accessible);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_action_get_n_actions(GObjHandle action, IntPtr error);

    [LibraryImport(LibAtspi)]
    public static partial int atspi_action_do_action(GObjHandle action, int i, IntPtr error);
}