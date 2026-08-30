using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Input;
using Everywhere.Automation;
using Everywhere.Utilities;
using Everywhere.Windows.Interop;
using Everywhere.Windows.Interop.UIAutomation;

namespace Everywhere.Windows.Automation;

/// <summary>
/// Represents one Context-owned Windows UI Automation element.
/// </summary>
public sealed class UIAutomationVisualElement(
    VisualContext context,
    WindowsVisualElementBackend backend,
    UIAutomationElementReference automationElement,
    string id,
    int processId,
    nint nativeWindowHandle,
    UIAutomationControlType controlType
) : VisualElement(context, id)
{
    private const int MaxNativeWindowAncestorDepth = 256;

    /// <summary>
    /// Gets the process ID of the UI Automation provider that exposes this element.
    /// </summary>
    public int ProcessId { get; } = processId;

    /// <summary>
    /// Gets the native window handle of the UI Automation element, if available.
    /// </summary>
    public nint NativeWindowHandle { get; } = nativeWindowHandle;

    /// <summary>
    /// Gets the native control type captured when this canonical element was first created.
    /// </summary>
    internal UIAutomationControlType ControlType { get; } = controlType;

    private UIAutomationElementReference AutomationElement =>
        _automationElement ?? throw new ObjectDisposedException(nameof(UIAutomationVisualElement));

    private WindowsVisualElementBackend Backend { get; } = backend;

    private UIAutomationElementReference? _automationElement = automationElement;

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => QueryCurrent(request);

    private VisualElementQueryResult QueryCurrent(VisualElementQueryRequest request)
    {
        try
        {
            using var cacheRequest = Backend.Automation.CreateElementCacheRequest(request.RequestedFields, false);
            using var cachedElement = AutomationElement.BuildUpdatedCache(cacheRequest);
            if (!cachedElement.HasValue)
            {
                throw new InvalidOperationException("UI Automation did not return an updated element cache.");
            }

            return cachedElement.CreateQueryResult(this, request);
        }
        catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
        {
            return new VisualElementQueryResult(
                this,
                default,
                VisualElementFields.None,
                request.RequestedFields,
                WindowsUIAutomationFailure.CreateFailure(exception));
        }
    }

    /// <inheritdoc />
    protected override IVisualElementEnumerator CreateEnumeratorCore(
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
    {
        return IsTopLevelWindow(NativeWindowHandle) && relation is VisualElementRelation.PreviousSibling or VisualElementRelation.NextSibling ?
            CreateTopLevelWindowSiblingEnumerator(relation, options.QueryRequest) :
            new UIAutomationVisualElementEnumerator(this, relation, options.QueryRequest);
    }

    private VisualElementQueryResult? QueryNext(
        VisualElementRetention retention,
        UIAutomationElementReference? lastElement,
        VisualElementRelation relation,
        VisualElementQueryRequest request)
    {
        if (lastElement is null && relation == VisualElementRelation.Parent && IsTopLevelWindow(NativeWindowHandle))
        {
            return QueryParentScreen(retention, request);
        }

        try
        {
            using var cacheRequest = Backend.Automation.CreateElementCacheRequest(request.RequestedFields);
            using var cachedElement = GetNextElement(lastElement, relation, cacheRequest);
            if (!cachedElement.HasValue)
            {
                return null;
            }

            var element = Backend.GetOrCreateUIAutomationElement(retention, in cachedElement);
            return cachedElement.CreateQueryResult(element, request);
        }
        catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
        {
            throw WindowsUIAutomationFailure.CreateException(exception);
        }
    }

    private UIAutomationElement GetNextElement(
        UIAutomationElementReference? lastElement,
        VisualElementRelation relation,
        UIAutomationCacheRequest cacheRequest)
    {
        if (lastElement is null)
        {
            return relation switch
            {
                VisualElementRelation.Parent => Backend.TreeWalker.NavigateBuildCache(
                    AutomationElement,
                    UIAutomationNavigationDirection.Parent,
                    cacheRequest),
                VisualElementRelation.Child => Backend.TreeWalker.NavigateBuildCache(
                    AutomationElement,
                    UIAutomationNavigationDirection.FirstChild,
                    cacheRequest),
                VisualElementRelation.PreviousSibling => Backend.TreeWalker.NavigateBuildCache(
                    AutomationElement,
                    UIAutomationNavigationDirection.PreviousSibling,
                    cacheRequest),
                VisualElementRelation.NextSibling => Backend.TreeWalker.NavigateBuildCache(
                    AutomationElement,
                    UIAutomationNavigationDirection.NextSibling,
                    cacheRequest),
                _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
            };
        }

        if (relation == VisualElementRelation.Parent)
        {
            return default;
        }

        return relation switch
        {
            VisualElementRelation.Child or VisualElementRelation.NextSibling => Backend.TreeWalker.NavigateBuildCache(
                lastElement,
                UIAutomationNavigationDirection.NextSibling,
                cacheRequest),
            VisualElementRelation.PreviousSibling => Backend.TreeWalker.NavigateBuildCache(
                lastElement,
                UIAutomationNavigationDirection.PreviousSibling,
                cacheRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
        };
    }

    private IVisualElementEnumerator CreateTopLevelWindowSiblingEnumerator(
        VisualElementRelation relation,
        VisualElementQueryRequest request)
    {
        var topology = WindowsDisplayTopology.Current;
        var display = topology.FindTopLevelWindowDisplay((HWND)NativeWindowHandle);
        if (display is null)
        {
            return new EmptyVisualElementEnumerator();
        }

        var direction = relation == VisualElementRelation.PreviousSibling ? GET_WINDOW_CMD.GW_HWNDPREV : GET_WINDOW_CMD.GW_HWNDNEXT;
        return new TopLevelWindowEnumerator(
            Context,
            Backend,
            topology,
            (HMONITOR)display.MonitorHandle,
            (HWND)NativeWindowHandle,
            false,
            direction,
            request);
    }

    private VisualElementQueryResult? QueryParentScreen(
        VisualElementRetention retention,
        VisualElementQueryRequest request)
    {
        var topology = WindowsDisplayTopology.Current;
        var display = topology.FindTopLevelWindowDisplay((HWND)NativeWindowHandle);
        return display is null ? null : Backend.GetOrCreateScreenElement(retention, topology, display).Query(request);
    }

    /// <inheritdoc />
    protected override void InvokeCore()
    {
        // TODO: Evaluate realization, visibility preparation, observable outcomes, and guarded input policies; see 07-Migration section 4.11.
        const UIAutomationCacheOptions options =
            UIAutomationCacheOptions.InvokePattern |
            UIAutomationCacheOptions.TogglePattern |
            UIAutomationCacheOptions.SelectionItemPattern |
            UIAutomationCacheOptions.ExpandCollapsePattern |
            UIAutomationCacheOptions.ExpandCollapseState |
            UIAutomationCacheOptions.LegacyIAccessiblePattern;
        using var cacheRequest = Backend.Automation.CreateCacheRequest(options);
        using var cachedElement = AutomationElement.BuildUpdatedCache(cacheRequest);
        if (!cachedElement.HasValue)
        {
            throw new InvalidOperationException("UI Automation did not return an updated element cache for invocation.");
        }

        if (!cachedElement.TryInvoke() &&
            !cachedElement.TryToggle() &&
            !cachedElement.TrySelect() &&
            !cachedElement.TryToggleExpansion() &&
            !cachedElement.TryDoLegacyDefaultAction())
        {
            if (!cachedElement.TryGetClickablePoint(out var clickablePoint))
            {
                throw new NotSupportedException(
                    "The UI Automation element exposes neither a supported standard invocation pattern nor a clickable point.");
            }

            var rootWindowHandle = GetRootOwnerWindow();
            Win32InputHelper.EnsureForegroundWindow(rootWindowHandle);
            Win32InputHelper.Click(new PixelPoint(clickablePoint.X, clickablePoint.Y), rootWindowHandle);
        }
    }

    /// <inheritdoc />
    protected override void SetTextCore(string text)
    {
        using var cacheRequest = Backend.Automation.CreateCacheRequest(
            UIAutomationCacheOptions.ValuePattern |
            UIAutomationCacheOptions.IsEnabled |
            UIAutomationCacheOptions.IsReadOnly);
        using var cachedElement = AutomationElement.BuildUpdatedCache(cacheRequest);
        if (!cachedElement.HasValue)
        {
            throw new InvalidOperationException("UI Automation did not return an updated element cache for text input.");
        }

        if (!cachedElement.CachedIsEnabled)
        {
            throw new InvalidOperationException("The UI Automation element is disabled and cannot accept text.");
        }

        if (cachedElement.GetCachedIsReadOnly())
        {
            throw new InvalidOperationException("The UI Automation element is read-only and cannot accept text.");
        }

        if (!cachedElement.TrySetValue(text))
        {
            throw new NotSupportedException("The UI Automation element does not expose an editable ValuePattern.");
        }
    }

    /// <inheritdoc />
    protected override void FocusCore() => FocusElement();

    private void FocusElement()
    {
        using var element = AutomationElement.Acquire();
        element.SetFocus();
    }

    /// <inheritdoc />
    protected override void SendKeyGestureCore(KeyGesture keyGesture) => SendKeyGestureToElement(keyGesture);

    /// <inheritdoc />
    protected override string? GetSelectedTextCore(int maxCharacters)
    {
        using var cacheRequest = Backend.Automation.CreateCacheRequest(
            UIAutomationCacheOptions.TextPattern | UIAutomationCacheOptions.SelectionPattern | UIAutomationCacheOptions.LegacyIAccessiblePattern);
        using var cachedElement = AutomationElement.BuildUpdatedCache(cacheRequest);
        if (!cachedElement.HasValue)
        {
            throw new InvalidOperationException("UI Automation did not return an updated element cache for selected text.");
        }

        return cachedElement.GetCachedSelectedText(maxCharacters);
    }

    private void SendKeyGestureToElement(KeyGesture keyGesture)
    {
        var rootWindowHandle = GetRootOwnerWindow();
        Win32InputHelper.EnsureForegroundWindow(rootWindowHandle);
        FocusElement();
        Win32InputHelper.EnsureForegroundWindow(rootWindowHandle);
        Win32InputHelper.SendKeyGesture(keyGesture);
    }

    private nint GetRootOwnerWindow()
    {
        return TryGetRootOwnerWindow(out var windowHandle) ?
            windowHandle :
            throw new InvalidOperationException("The target element does not belong to a valid root window.");
    }

    internal bool TryGetRootOwnerWindow(out nint rootWindowHandle)
    {
        var windowHandle = NativeWindowHandle;
        if (windowHandle == 0)
        {
            using var cacheRequest = Backend.Automation.CreateCacheRequest(UIAutomationCacheOptions.NativeWindowHandle);
            using var cachedElement = AutomationElement.BuildUpdatedCache(cacheRequest);
            if (!cachedElement.HasValue)
            {
                throw new InvalidOperationException("UI Automation did not return an updated element cache for native-window resolution.");
            }

            windowHandle = cachedElement.CachedNativeWindowHandle;
            if (windowHandle == 0)
            {
                windowHandle = FindFirstAncestorNativeWindowHandle(cacheRequest, MaxNativeWindowAncestorDepth);
            }
        }

        if (windowHandle == 0)
        {
            rootWindowHandle = 0;
            return false;
        }

        var rootWindow = PInvoke.GetAncestor((HWND)windowHandle, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        rootWindowHandle = rootWindow;
        return rootWindow != HWND.Null;
    }

    private nint FindFirstAncestorNativeWindowHandle(
        UIAutomationCacheRequest cacheRequest,
        int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        var current = Backend.TreeWalker.NavigateBuildCache(AutomationElement, UIAutomationNavigationDirection.Parent, cacheRequest);
        var depth = 0;
        try
        {
            while (current.HasValue)
            {
                if (++depth > maxDepth)
                {
                    throw new InvalidOperationException($"UI Automation native-window resolution exceeded the {maxDepth}-ancestor limit.");
                }

                var windowHandle = current.CachedNativeWindowHandle;
                if (windowHandle != 0)
                {
                    return windowHandle;
                }

                var next = Backend.TreeWalker.NavigateBuildCache(in current, UIAutomationNavigationDirection.Parent, cacheRequest);
                current.Dispose();
                current = next;
            }

            return 0;
        }
        finally
        {
            current.Dispose();
        }
    }

    // BUG: For a minimized window, the captured image is buggy (but child elements are fine).
    /// <inheritdoc />
    protected override async Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        using var retention = Context.CreateRetention();
        retention.Retain(this);
        var request = new VisualElementQueryRequest(VisualElementFields.Bounds | VisualElementFields.NativeWindowHandle, 0);
        var source = Query(request);
        var sourceBounds = source.Snapshot.Bounds ?? throw new InvalidOperationException("The visual element does not expose capture bounds.");
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0)
        {
            throw new InvalidOperationException("Cannot capture an element with zero width or height.");
        }

        var current = source;
        var visitedIds = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedIds.Add(current.Element.Id))
            {
                throw new InvalidOperationException("The UI Automation parent chain contains a cycle.");
            }

            var windowHandle = current.Snapshot.NativeWindowHandle.GetValueOrDefault();
            if (IsTopLevelWindow(windowHandle))
            {
                var windowBounds = current.Snapshot.Bounds ??
                    throw new InvalidOperationException("The top-level window does not expose capture bounds.");
                return await Direct3D11ScreenCapture.CaptureAsync(
                    windowHandle,
                    new PixelRect(sourceBounds.X - windowBounds.X, sourceBounds.Y - windowBounds.Y, sourceBounds.Width, sourceBounds.Height),
                    cancellationToken);
            }

            using var parents = current.Element.CreateEnumerator(
                VisualElementRelation.Parent,
                new VisualElementEnumerationOptions(request));
            if (!parents.MoveNext())
            {
                throw new InvalidOperationException("Failed to find the top-level window for the visual element.");
            }

            retention.Retain(parents.Current.Element);
            current = parents.Current;
        }
    }

    /// <inheritdoc />
    protected override void ReleaseCore() => DisposeHelper.DisposeToDefault(ref _automationElement);

    /// <inheritdoc />
    protected override bool TryConvertPlatformException(Exception exception, [NotNullWhen(true)] out Exception? convertedException)
    {
        if (WindowsUIAutomationFailure.IsProviderException(exception))
        {
            convertedException = WindowsUIAutomationFailure.CreateException(exception);
            return true;
        }

        convertedException = null;
        return false;
    }

    internal static bool IsTopLevelWindow(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return false;
        }

        var style = PInvoke.GetWindowLong((HWND)windowHandle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return (style & (int)WINDOW_STYLE.WS_CHILD) == 0;
    }

    private sealed class UIAutomationVisualElementEnumerator : IVisualElementEnumerator
    {
        public VisualElementQueryResult Current
        {
            get
            {
                ThrowIfUnavailable();
                return _current ?? throw new InvalidOperationException("The Enumerator has no current item.");
            }
        }

        object IEnumerator.Current => Current;

        public int Count
        {
            get
            {
                ThrowIfUnavailable();
                return -1;
            }
        }

        public int Index { get; private set; } = -1;

        private UIAutomationVisualElement? _origin;
        private readonly VisualElementRetention _retention;
        private readonly VisualElementRelation _relation;
        private readonly VisualElementQueryRequest _queryRequest;
        private UIAutomationElementReference? _lastElement;
        private VisualElementQueryResult? _lookahead;
        private VisualElementQueryResult? _current;
        private int _nextIndex;
        private bool _isLookaheadResolved;
        private bool _isCompleted;
        private bool _isDisposed;

        internal UIAutomationVisualElementEnumerator(
            UIAutomationVisualElement origin,
            VisualElementRelation relation,
            VisualElementQueryRequest queryRequest)
        {
            _origin = origin;
            _relation = relation;
            _queryRequest = queryRequest;
            _retention = origin.Context.CreateRetention();
            _retention.Retain(origin);
        }

        public bool HasMore
        {
            get
            {
                ThrowIfUnavailable();
                EnsureLookahead();
                return _lookahead is not null;
            }
        }

        public bool MoveNext()
        {
            ThrowIfUnavailable();
            EnsureLookahead();
            if (_lookahead is not { } next)
            {
                _current = null;
                Index = -1;
                return false;
            }

            _lookahead = null;
            _isLookaheadResolved = false;
            _current = next;
            _lastElement = (next.Element as UIAutomationVisualElement)?.AutomationElement;
            _isCompleted = _relation == VisualElementRelation.Parent;
            Index = _nextIndex;
            _nextIndex++;
            return true;
        }

        public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _origin = null;
            _lastElement = null;
            _lookahead = null;
            _current = null;
            _retention.Dispose();
        }

        private void EnsureLookahead()
        {
            if (_isLookaheadResolved || _isCompleted)
            {
                return;
            }

            var currentOrigin = _origin ?? throw new ObjectDisposedException(nameof(UIAutomationVisualElementEnumerator));
            _lookahead = currentOrigin.QueryNext(_retention, _lastElement, _relation, _queryRequest);
            _isLookaheadResolved = true;
            _isCompleted = _lookahead is null;
        }

        private void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }
    }
}