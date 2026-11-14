using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Everywhere.Automation;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Automation;

/// <summary>
/// A retained AT-SPI proxy. Its native reference prevents pointer reuse while this identity is alive.
/// </summary>
public sealed class AtspiVisualElement : VisualElement
{
    private readonly LinuxVisualElementBackend _backend;
    private readonly CompositorWindowInfo _window;
    private readonly nint _pointer;
    private bool _canReadGeometry = true;

    private AtspiVisualElement(
        VisualElementIdentity identity,
        LinuxVisualElementBackend backend,
        CompositorWindowInfo window,
        nint pointer) : base(identity, backend.AllocateId())
    {
        _backend = backend;
        _window = window;
        _canReadGeometry = backend.Accessibility.Call(() => !AtspiNative.IsDetachedEmbeddedRoot(pointer));
        _pointer = backend.Accessibility.Call(() => AtspiNative.g_object_ref(pointer));
    }

    /// <summary>
    /// Retains the native target in the context identity map for its compositor window.
    /// </summary>
    public static AtspiVisualElement GetOrCreate(
        VisualElementRetention retention,
        LinuxVisualElementBackend backend,
        CompositorWindowInfo window,
        nint pointer)
    {
        return retention.Context.GetIdentityMap<NativeIdentity>()
            .GetOrAdd(
                retention,
                new NativeIdentity(backend, window.Id, pointer),
                (Backend: backend, Window: window, Pointer: pointer),
                static (identity, state) => new AtspiVisualElement(identity, state.Backend, state.Window, state.Pointer));
    }

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
    {
        return _backend.Accessibility.Call(() =>
        {
            var fields = request.RequestedFields;
            var role = AtspiNative.Role(_pointer);
            var states = ReadStates(role);
            var text = fields.HasFlag(VisualElementFields.Text) && !states.HasFlag(VisualElementStates.Password)
                ? ReadNativeText(0, Math.Max(1, request.MaxTextCharacters), Math.Max(2, request.MaxTextCharacters * 2 + 2))
                : null;
            var available = fields & ~(VisualElementFields.Text | VisualElementFields.Bounds);
            if (text?.Text is not null)
            {
                available |= fields & VisualElementFields.Text;
            }

            var bounds = default(PixelRect?);
            var failure = text?.Failure?.Kind == VisualElementQueryFailureKind.Unsupported ? null : text?.Failure;
            // Do not repeat one embedded provider's coordinate timeout for every child.
            // Missing Bounds is still reported independently of readable semantic fields.
            if (fields.HasFlag(VisualElementFields.Bounds) && !_canReadGeometry && role == "filler")
            {
                failure ??= new VisualElementQueryFailure(
                    VisualElementQueryFailureKind.Unsupported,
                    null,
                    agentMessage: "The embedded accessibility tree exposes semantics without safe window-relative coordinates.");
            }

            if (fields.HasFlag(VisualElementFields.Bounds) && _canReadGeometry && !_backend.IsBoundsQueryBackedOff(_window))
            {
                try
                {
                    bounds = ReadBounds();
                }
                catch (TimeoutException exception)
                {
                    _backend.RecordBoundsTimeout(_window);
                    failure = new VisualElementQueryFailure(
                        VisualElementQueryFailureKind.Timeout,
                        null,
                        exception,
                        "The application's control-bounds provider timed out; semantic fields remain readable.");
                }
            }

            if (bounds is not null)
            {
                available |= VisualElementFields.Bounds;
            }

            var snapshot = new VisualElementSnapshot(
                Id,
                TypeFor(role),
                states,
                fields.HasFlag(VisualElementFields.Name) ? AtspiNative.Name(_pointer) : null,
                text?.Text,
                text?.HasMoreText ?? false,
                bounds,
                _window.ProcessId,
                _window.Handle)
            {
                TotalTextLength = text?.TotalLength,
            };
            return new VisualElementQueryResult(this, snapshot, available, fields & ~available, failure);
        });
    }

    /// <inheritdoc />
    protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters, int maximumProbeCharacters)
    {
        return _backend.Accessibility.Call(() => ReadNativeText(offset, maxCharacters, maximumProbeCharacters));
    }

    private VisualElementTextReadResult ReadNativeText(int offset, int maxCharacters, int maximumProbeCharacters)
    {
        if (AtspiNative.Role(_pointer) == "password text")
        {
            return new(null, null, new(VisualElementQueryFailureKind.Unsupported, null));
        }

        var textInterface = AtspiNative.atspi_accessible_get_text_iface(_pointer);
        if (textInterface == 0)
        {
            return new(null, null, new(VisualElementQueryFailureKind.Unsupported, null));
        }

        try
        {
            var count = AtspiNative.Checked(AtspiNative.atspi_text_get_character_count(textInterface, out var error), error);
            // Native offsets count Unicode characters; the shared contract counts UTF-16 code units.
            // A bounded prefix establishes their correspondence without splitting surrogate pairs.
            var characters = Math.Min(count, maximumProbeCharacters / 2);
            var prefix = AtspiNative.String(AtspiNative.atspi_text_get_text(textInterface, 0, characters, out error), error);
            if (characters < count && (offset < 0 || offset >= prefix.Length))
            {
                return new(
                    null,
                    null,
                    new(
                        VisualElementQueryFailureKind.LimitReached,
                        null,
                        agentMessage: "The requested UTF-16 offset exceeds the bounded native prefix."),
                    VisualTextLength.LowerBound(prefix.Length));
            }

            var page = VisualElementTextReadResult.FromSuccess(prefix, offset, maxCharacters);
            return characters == count ? page : page with
            {
                TotalLength = VisualTextLength.LowerBound(prefix.Length),
                NextOffset = page.NextOffset ?? page.Offset + (page.Text?.Length ?? 0),
            };
        }
        finally
        {
            AtspiNative.g_object_unref(textInterface);
        }
    }

    /// <inheritdoc />
    protected override IVisualElementCursor CreateEnumeratorCore(
        VisualElementRelation relation,
        VisualElementQueryRequest request,
        int offset,
        CancellationToken cancellationToken)
    {
        return new LinuxVisualCursor(Context, retention => Enumerate(retention, relation, cancellationToken), request, offset, cancellationToken);
    }

    private IEnumerable<VisualElement> Enumerate(
        VisualElementRetention retention,
        VisualElementRelation relation,
        CancellationToken cancellationToken)
    {
        if (relation == VisualElementRelation.Child)
        {
            var count = _backend.Accessibility.Call(() => AtspiNative.Count(_pointer));
            var deadline = Stopwatch.StartNew();
            for (var index = 0; index < count; index++)
            {
                CheckRelationBudget(index, deadline, cancellationToken);
                var pointer = _backend.Accessibility.Call(() => AtspiNative.Child(_pointer, index));
                if (pointer == 0)
                {
                    continue;
                }

                try
                {
                    yield return GetChild(retention, pointer);
                }
                finally
                {
                    _backend.Accessibility.Release(pointer);
                }

                // The next MoveNext owns a fresh time slice; time spent by the
                // caller observing other nodes must not consume this native call's budget.
                deadline.Restart();
            }

            yield break;
        }

        var parent = _backend.Accessibility.Call(() => AtspiNative.Checked(AtspiNative.atspi_accessible_get_parent(_pointer, out var error), error));
        if (parent == 0)
        {
            yield break;
        }

        try
        {
            var isApplication = _backend.Accessibility.Call(() => AtspiNative.Role(parent) == "application");
            if (relation == VisualElementRelation.Parent)
            {
                yield return isApplication ? _backend.GetWindow(retention, _window) : GetOrCreate(retention, _backend, _window, parent);
                yield break;
            }

            if (isApplication)
            {
                yield break;
            }

            var position = _backend.Accessibility.Call(
                (
                    ) => AtspiNative.Checked(AtspiNative.atspi_accessible_get_index_in_parent(_pointer, out var error), error));
            var count = _backend.Accessibility.Call(() => AtspiNative.Count(parent));
            var step = relation == VisualElementRelation.PreviousSibling ? -1 : 1;
            var deadline = Stopwatch.StartNew();
            var candidates = 0;
            for (var index = position + step; index >= 0 && index < count; index += step)
            {
                CheckRelationBudget(candidates++, deadline, cancellationToken);
                var pointer = _backend.Accessibility.Call(() => AtspiNative.Child(parent, index));
                if (pointer == 0)
                {
                    continue;
                }

                try
                {
                    yield return GetChild(retention, pointer);
                }
                finally
                {
                    _backend.Accessibility.Release(pointer);
                }

                deadline.Restart();
            }
        }
        finally
        {
            _backend.Accessibility.Release(parent);
        }
    }

    private static void CheckRelationBudget(int candidates, Stopwatch deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates >= AtspiQueryLimits.MaximumNodes || deadline.ElapsedMilliseconds >= AtspiQueryLimits.MaximumElapsedMilliseconds)
        {
            throw new VisualElementProviderException(
                VisualElementQueryFailureKind.LimitReached,
                "Native relation enumeration reached its candidate/time limit; the returned relation is partial.");
        }
    }

    /// <inheritdoc />
    protected override void InvokeCore()
    {
        _backend.Accessibility.Call(() =>
        {
            var action = AtspiNative.atspi_accessible_get_action_iface(_pointer);
            if (action == 0)
            {
                throw new NotSupportedException("The control has no semantic action.");
            }

            try
            {
                if (AtspiNative.Checked(AtspiNative.atspi_action_do_action(action, 0, out var error), error) == 0)
                {
                    throw new NotSupportedException("The control rejected its default action.");
                }

                return true;
            }
            finally
            {
                AtspiNative.g_object_unref(action);
            }
        });
    }

    /// <inheritdoc />
    protected override void SetTextCore(string text)
    {
        _backend.Accessibility.Call(() =>
        {
            var editable = AtspiNative.atspi_accessible_get_editable_text_iface(_pointer);
            if (editable == 0)
            {
                throw new NotSupportedException("The control is not editable.");
            }

            try
            {
                if (AtspiNative.Checked(AtspiNative.atspi_editable_text_set_text_contents(editable, text, out var error), error) == 0)
                {
                    throw new NotSupportedException("The control rejected text replacement.");
                }

                return true;
            }
            finally
            {
                AtspiNative.g_object_unref(editable);
            }
        });
    }

    /// <inheritdoc />
    protected override void FocusCore()
    {
        _backend.Accessibility.Call(() =>
        {
            var component = AtspiNative.atspi_accessible_get_component_iface(_pointer);
            if (component == 0)
            {
                throw new NotSupportedException("The control cannot receive focus.");
            }

            try
            {
                return AtspiNative.Checked(AtspiNative.atspi_component_grab_focus(component, out var error), error) != 0;
            }
            finally
            {
                AtspiNative.g_object_unref(component);
            }
        });
    }

    /// <inheritdoc />
    protected override string? GetSelectedTextCore(int maxCharacters)
    {
        return _backend.Accessibility.Call(() =>
        {
            if (AtspiNative.Role(_pointer) == "password text")
            {
                return null;
            }

            var text = AtspiNative.atspi_accessible_get_text_iface(_pointer);
            if (text == 0)
            {
                return null;
            }

            try
            {
                if (AtspiNative.Checked(AtspiNative.atspi_text_get_n_selections(text, out var error), error) == 0)
                {
                    return null;
                }

                var range = AtspiNative.Checked(AtspiNative.atspi_text_get_selection(text, 0, out error), error);
                if (range == 0)
                {
                    return null;
                }

                try
                {
                    var start = Marshal.ReadInt32(range);
                    var end = Math.Min(Marshal.ReadInt32(range, 4), start + maxCharacters / 2);
                    return AtspiNative.String(AtspiNative.atspi_text_get_text(text, start, end, out error), error);
                }
                finally
                {
                    AtspiNative.g_free(range);
                }
            }
            finally
            {
                AtspiNative.g_object_unref(text);
            }
        });
    }

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        return _backend.CaptureAsync(
            _backend.Accessibility.Call(ReadBounds)
            ?? throw new NotSupportedException("The control has no usable bounds."),
            cancellationToken);
    }

    /// <inheritdoc />
    protected override VisualElement AdoptCore(VisualElementRetention destinationRetention)
    {
        var adopted = GetOrCreate(destinationRetention, _backend, _window, _pointer);
        adopted._canReadGeometry &= _canReadGeometry;
        return adopted;
    }

    /// <inheritdoc />
    protected override void ReleaseCore() => _backend.Accessibility.Release(_pointer);
    private AtspiVisualElement GetChild(VisualElementRetention retention, nint pointer)
    {
        var child = GetOrCreate(retention, _backend, _window, pointer);
        if (!_canReadGeometry)
        {
            child._canReadGeometry = false;
        }

        return child;
    }

    private PixelRect? ReadBounds()
    {
        if (!_canReadGeometry)
        {
            throw new NotSupportedException("The embedded provider has no safe window-relative geometry; capture its compositor window instead.");
        }

        var window = _backend.ReadDesktop().Windows.FirstOrDefault(item => item.Id == _window.Id && item.ProcessId == _window.ProcessId);
        if (window is null)
        {
            throw new VisualElementProviderException(VisualElementQueryFailureKind.ElementUnavailable, "The source window disappeared.");
        }

        var rectangle = AtspiNative.ReadExtents(_pointer, AtspiCoordinates.Window);
        return rectangle is { } bounds
            ? new PixelRect(
                checked(window.ClientRect.X + bounds.X),
                checked(window.ClientRect.Y + bounds.Y),
                bounds.Width,
                bounds.Height)
            : null;
    }

    private VisualElementStates ReadStates(string role)
    {
        var set = AtspiNative.atspi_accessible_get_state_set(_pointer);
        if (set == 0)
        {
            return VisualElementStates.None;
        }

        try
        {
            return MapStates(role, state => AtspiNative.atspi_state_set_contains(set, state) != 0);
        }
        finally
        {
            AtspiNative.g_object_unref(set);
        }
    }

    private static VisualElementStates MapStates(string role, Func<AtspiState, bool> hasState)
    {
        if (hasState(AtspiState.Defunct))
        {
            throw new VisualElementProviderException(VisualElementQueryFailureKind.ElementUnavailable, "The accessibility object is defunct.");
        }

        var states = VisualElementStates.None;
        if (!hasState(AtspiState.Showing) || !hasState(AtspiState.Visible))
        {
            states |= VisualElementStates.Offscreen;
        }

        if (!hasState(AtspiState.Enabled) && !hasState(AtspiState.Sensitive))
        {
            states |= VisualElementStates.Disabled;
        }

        if (hasState(AtspiState.Focused))
        {
            states |= VisualElementStates.Focused;
        }

        if (hasState(AtspiState.Selected))
        {
            states |= VisualElementStates.Selected;
        }

        // Non-editable containers are not read-only values. Fabricating ReadOnly
        // keeps empty wrappers in the shared projection and spends its node budget.
        if (hasState(AtspiState.ReadOnly))
        {
            states |= VisualElementStates.ReadOnly;
        }

        if (role == "password text")
        {
            states |= VisualElementStates.Password;
        }

        return states;
    }

    private static VisualElementType TypeFor(string role)
    {
        return role switch
        {
            // The compositor owns TopLevel; its provider frame is the child container.
            "frame" or "window" => VisualElementType.Panel,
            "push button" or "button" => VisualElementType.Button,
            "text" or "entry" or "password text" => VisualElementType.TextEdit,
            "label" or "static" or "paragraph" => VisualElementType.Label,
            "document web" or "document text" or "document frame" => VisualElementType.Document,
            "check box" or "toggle button" => VisualElementType.CheckBox,
            "radio button" => VisualElementType.RadioButton,
            "combo box" => VisualElementType.ComboBox,
            "list" => VisualElementType.ListView,
            "list item" => VisualElementType.ListViewItem,
            "menu" or "menu bar" => VisualElementType.Menu,
            "menu item" or "check menu item" or "radio menu item" => VisualElementType.MenuItem,
            "tree" => VisualElementType.TreeView,
            "tree item" => VisualElementType.TreeViewItem,
            "tree table" => VisualElementType.DataGrid,
            "heading" => VisualElementType.Header,
            "column header" or "row header" or "table column header" or "table row header" => VisualElementType.HeaderItem,
            "progress bar" => VisualElementType.ProgressBar,
            "spin button" => VisualElementType.Spinner,
            "tool bar" => VisualElementType.ToolBar,
            "status bar" => VisualElementType.StatusBar,
            "table" => VisualElementType.Table,
            "table row" => VisualElementType.TableRow,
            "table cell" => VisualElementType.DataGridItem,
            "slider" => VisualElementType.Slider,
            "scroll bar" => VisualElementType.ScrollBar,
            "page tab" => VisualElementType.TabItem,
            "page tab list" => VisualElementType.TabControl,
            "link" => VisualElementType.Hyperlink,
            "image" or "icon" => VisualElementType.Image,
            _ => VisualElementType.Panel,
        };
    }

    private sealed record NativeIdentity(LinuxVisualElementBackend Backend, string WindowAddress, nint Pointer);
}