using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Input;
using Everywhere.Automation.Testing;
using Everywhere.I18N;
using VisualContextLocaleKey = Everywhere.Automation.I18N.LocaleKey;
using LiveVisualContext = Everywhere.Automation.VisualContext;

namespace Everywhere.Automation.Tests.Testing;

internal sealed class ScenarioMockBackend : IDisposable
{
    public LiveVisualContext Context { get; }

    public ScenarioMockOperations Operations { get; } = new();

    public ScenarioVisualElement RootElement => _roots.Count == 1 ?
        _roots[0] :
        throw new InvalidOperationException($"Scenario '{_scenario.Name}' contains {_roots.Count} roots.");

    public IReadOnlyList<ScenarioVisualElement> RootElements => _roots;

    public long Step => Interlocked.Read(ref _step);

    internal bool HasKnownCount { get; }

    internal VisualElementFields SupportedFields { get; }

    private readonly GeneratedVisualScenario _scenario;
    private readonly IReadOnlyList<ScenarioVisualElement> _roots;
    private readonly Dictionary<string, ScenarioVisualElement> _elements = [];
    private readonly VisualElementIdentityMap<string> _identityMap;
    private readonly VisualElementRetention _retention;
    private readonly Dictionary<string, string> _textOverrides = [];
    private readonly Func<string, VisualElementQueryFailure?>? _failureProvider;
    private long _step;

    public ScenarioMockBackend(
        GeneratedVisualScenario scenario,
        bool hasCount = true,
        VisualElementFields supportedFields = VisualElementFields.All,
        Func<string, VisualElementQueryFailure?>? failureProvider = null)
    {
        Context = new LiveVisualContext();
        _identityMap = Context.GetIdentityMap<string>(StringComparer.Ordinal);
        _retention = Context.CreateRetention();
        _scenario = scenario;
        HasKnownCount = hasCount;
        SupportedFields = supportedFields;
        _failureProvider = failureProvider;
        if (scenario.Roots.Count == 0)
        {
            throw new ArgumentException("A visual scenario must contain at least one root.", nameof(scenario));
        }

        var roots = new ScenarioVisualElement[scenario.Roots.Count];
        for (var i = 0; i < roots.Length; i++)
        {
            var rootIndex = i;
            var rootId = CreateRootId(i);
            var root = _identityMap.GetOrAdd(_retention, rootId, (Backend: this, RootIndex: rootIndex), static (id, state) => new ScenarioVisualElement(state.Backend, null, -1, id, () => state.Backend._scenario.Roots[state.RootIndex]));
            roots[i] = root;
            _elements.Add(root.Id, root);
            Operations.ElementCreated();
        }

        _roots = roots;
    }

    public void Dispose() => Context.Dispose();

    public ScenarioVisualElement GetElement(params IReadOnlyList<int> path)
    {
        var current = _roots[0];
        foreach (var index in path)
        {
            current = GetChild(current, index);
        }

        return current;
    }

    public ScenarioVisualElement GetRoot(int index) => _roots[index];

    internal VisualControl Resolve(VisualControl control)
    {
        while (control is OnMoveNext mutation)
        {
            control = mutation.Resolve(Step);
        }

        return control;
    }

    internal ScenarioVisualElement GetChild(ScenarioVisualElement parent, int index)
    {
        var id = $"{parent.Id}/{index}";
        lock (_elements)
        {
            if (_elements.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var element = _identityMap.GetOrAdd(_retention, id, (Backend: this, Parent: parent, Index: index), static (identity, state) => new ScenarioVisualElement(state.Backend, state.Parent, state.Index, identity, () => state.Parent.Control.GetChild(state.Index)));
            _elements.Add(id, element);
            Operations.ElementCreated();
            return element;
        }
    }

    internal long AdvanceStep()
    {
        Operations.MoveNextAttempted();
        return Interlocked.Increment(ref _step);
    }

    internal string? GetText(ScenarioVisualElement element, VisualControl control)
    {
        lock (_textOverrides)
        {
            return _textOverrides.TryGetValue(element.Id, out var value) ? value : control.TextContent;
        }
    }

    internal void SetText(ScenarioVisualElement element, string text)
    {
        lock (_textOverrides)
        {
            _textOverrides[element.Id] = text;
        }

        Operations.ActionInvoked();
    }

    internal VisualElementQueryFailure? GetFailure(ScenarioVisualElement element) => _failureProvider?.Invoke(element.Id);

    private string CreateRootId(int index) =>
        _scenario.Roots.Count == 1 ? $"{_scenario.Name}:{_scenario.Seed}" : $"{_scenario.Name}:{_scenario.Seed}/root/{index}";

}

internal sealed class ScenarioMockOperations
{
    public int ScopeCreatedCount => Volatile.Read(ref _scopeCreatedCount);

    public int ScopeDisposedCount => Volatile.Read(ref _scopeDisposedCount);

    public int ScalarQueryCount => Volatile.Read(ref _scalarQueryCount);

    public int MoveNextAttemptCount => Volatile.Read(ref _moveNextAttemptCount);

    public int EnumeratorCreatedCount => Volatile.Read(ref _enumeratorCreatedCount);

    public int EnumeratorDisposedCount => Volatile.Read(ref _enumeratorDisposedCount);

    public int ElementCreatedCount => Volatile.Read(ref _elementCreatedCount);

    public int ElementReleasedCount => Volatile.Read(ref _elementReleasedCount);

    public int ActionCount => Volatile.Read(ref _actionCount);

    private int _scopeCreatedCount;
    private int _scopeDisposedCount;
    private int _scalarQueryCount;
    private int _moveNextAttemptCount;
    private int _enumeratorCreatedCount;
    private int _enumeratorDisposedCount;
    private int _elementCreatedCount;
    private int _elementReleasedCount;
    private int _actionCount;

    internal void ScopeCreated() => Interlocked.Increment(ref _scopeCreatedCount);

    internal void ScopeDisposed() => Interlocked.Increment(ref _scopeDisposedCount);

    internal void ScalarQuery() => Interlocked.Increment(ref _scalarQueryCount);

    internal void MoveNextAttempted() => Interlocked.Increment(ref _moveNextAttemptCount);

    internal void EnumeratorCreated() => Interlocked.Increment(ref _enumeratorCreatedCount);

    internal void EnumeratorDisposed() => Interlocked.Increment(ref _enumeratorDisposedCount);

    internal void ElementCreated() => Interlocked.Increment(ref _elementCreatedCount);

    internal void ElementReleased() => Interlocked.Increment(ref _elementReleasedCount);

    internal void ActionInvoked() => Interlocked.Increment(ref _actionCount);
}

internal sealed class ScenarioMockQueryEnumerator(
    ScenarioVisualElement origin,
    VisualElementRelation relation,
    VisualElementEnumerationOptions options
) : IVisualElementEnumerator
{
    /// <inheritdoc />
    public VisualElementQueryResult Current => _current ?? throw new InvalidOperationException("The enumerator has no current item.");

    object IEnumerator.Current => Current;

    /// <inheritdoc />
    public int Count => _navigator.Count;

    /// <inheritdoc />
    public int Index => _navigator.Index;

    /// <inheritdoc />
    public bool HasMore => _navigator.HasMore;

    private readonly ScenarioRelationNavigator _navigator = new(origin, relation);
    private readonly VisualElementQueryRequest _queryRequest = options.QueryRequest;
    private VisualElementQueryResult? _current;
    private bool _isDisposed;

    /// <inheritdoc />
    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_navigator.MoveNext())
        {
            _current = null;
            return false;
        }

        _current = _navigator.Current.Query(_queryRequest);
        return true;
    }

    /// <inheritdoc />
    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _navigator.Dispose();
    }
}

internal sealed class ScenarioRelationNavigator : IEnumerator<ScenarioVisualElement>
{
    /// <inheritdoc />
    public ScenarioVisualElement Current =>
        _current ?? throw new InvalidOperationException("The enumerator has no current item.");

    object IEnumerator.Current => Current;

    public int Count => _origin.Backend.HasKnownCount ? _initialCount : -1;

    public int Index { get; private set; } = -1;

    public bool HasMore
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return !_isCompleted && HasTarget(_nextOrdinal);
        }
    }

    private readonly ScenarioVisualElement _origin;
    private readonly VisualElementRelation _relation;
    private readonly int _initialCount;
    private ScenarioVisualElement? _current;
    private int _nextOrdinal;
    private bool _isCompleted;
    private bool _isDisposed;

    public ScenarioRelationNavigator(ScenarioVisualElement origin, VisualElementRelation relation)
    {
        _origin = origin;
        _relation = relation;
        _initialCount = GetCount();
        origin.Backend.Operations.EnumeratorCreated();
    }

    /// <inheritdoc />
    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _origin.Backend.AdvanceStep();

        if (_isCompleted || !HasTarget(_nextOrdinal))
        {
            _isCompleted = true;
            _current = null;
            Index = -1;
            return false;
        }

        _current = GetTarget(_nextOrdinal);
        Index = _nextOrdinal;
        _nextOrdinal++;
        return true;
    }

    /// <inheritdoc />
    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _origin.Backend.Operations.EnumeratorDisposed();
    }

    private int GetCount()
    {
        return _relation switch
        {
            VisualElementRelation.Parent => _origin.ParentElement is null ? 0 : 1,
            VisualElementRelation.Child => _origin.TryGetControl(out var control) ? control.ChildCount : 0,
            VisualElementRelation.PreviousSibling => _origin.ParentElement is null ? 0 : _origin.SiblingIndex,
            VisualElementRelation.NextSibling => GetNextSiblingCount(),
            _ => throw new ArgumentOutOfRangeException(nameof(_relation), _relation, null),
        };
    }

    private bool HasTarget(int ordinal)
    {
        if (ordinal < 0)
        {
            return false;
        }

        return _relation switch
        {
            VisualElementRelation.Parent => ordinal == 0 && _origin.ParentElement is not null,
            VisualElementRelation.Child =>
                _origin.TryGetControl(out var control) && ordinal < control.ChildCount,
            VisualElementRelation.PreviousSibling =>
                _origin.ParentElement is not null && ordinal < _origin.SiblingIndex,
            VisualElementRelation.NextSibling => ordinal < GetNextSiblingCount(),
            _ => throw new ArgumentOutOfRangeException(nameof(_relation), _relation, null),
        };
    }

    private ScenarioVisualElement GetTarget(int ordinal)
    {
        return _relation switch
        {
            VisualElementRelation.Parent => GetParent(),
            VisualElementRelation.Child => _origin.Backend.GetChild(_origin, ordinal),
            VisualElementRelation.PreviousSibling =>
                _origin.Backend.GetChild(GetParent(), _origin.SiblingIndex - ordinal - 1),
            VisualElementRelation.NextSibling =>
                _origin.Backend.GetChild(GetParent(), _origin.SiblingIndex + ordinal + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(_relation), _relation, null),
        };
    }

    private int GetNextSiblingCount()
    {
        var parent = _origin.ParentElement;
        return parent is not null && parent.TryGetControl(out var control) ? Math.Max(0, control.ChildCount - _origin.SiblingIndex - 1) : 0;
    }

    private ScenarioVisualElement GetParent() =>
        _origin.ParentElement ?? throw new InvalidOperationException("The element has no parent.");
}

internal sealed class ScenarioVisualElement(
    ScenarioMockBackend backend,
    ScenarioVisualElement? parent,
    int siblingIndex,
    string id,
    Func<VisualControl> controlResolver
) : VisualElement(backend.Context, id)
{
    public PixelRect BoundingRectangle =>
        ParentElement is null ? new PixelRect(0, 0, 1280, 720) : new PixelRect(SiblingIndex * 8, GetDepth() * 24, 320, 20);

    public int ProcessId => 10_001;

    public nint NativeWindowHandle => 1;

    internal ScenarioMockBackend Backend { get; } = backend;

    internal ScenarioVisualElement? ParentElement { get; } = parent;

    internal int SiblingIndex { get; } = siblingIndex;

    internal VisualControl Control =>
        TryGetControl(out var control) ? control : throw new InvalidOperationException($"Element '{Id}' is no longer available.");

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => QueryDirect(request);

    private VisualElementQueryResult QueryDirect(VisualElementQueryRequest request)
    {
        Backend.Operations.ScalarQuery();

        var providerFailure = Backend.GetFailure(this);
        if (providerFailure is not null)
        {
            return CreateFailure(request.RequestedFields, providerFailure);
        }

        if (!TryGetControl(out var control))
        {
            return CreateFailure(
                    request.RequestedFields,
                    new VisualElementQueryFailure(
                        VisualElementQueryFailureKind.ElementUnavailable,
                        new DynamicLocaleKey(VisualContextLocaleKey.VisualContext_QueryFailure_ElementUnavailable)));
        }

        var fields = request.RequestedFields & Backend.SupportedFields;
        var missingFields = request.RequestedFields & ~fields;
        var completeText = HasField(fields, VisualElementFields.Text) ? Backend.GetText(this, control) : null;
        var hasMoreText = completeText is { Length: var textLength } && textLength > request.MaxTextCharacters;
        var text = GetBoundedText(completeText, request.MaxTextCharacters);
        var snapshot = new VisualElementSnapshot(
            HasField(fields, VisualElementFields.Id) ? Id : null,
            HasField(fields, VisualElementFields.Type) ? MapType(control.Kind) : null,
            HasField(fields, VisualElementFields.States) ? MapStates(control.States) : null,
            HasField(fields, VisualElementFields.Name) ? control.Name : null,
            text,
            hasMoreText,
            HasField(fields, VisualElementFields.Bounds) ? BoundingRectangle : null,
            HasField(fields, VisualElementFields.ProcessId) ? ProcessId : null,
            HasField(fields, VisualElementFields.NativeWindowHandle) ? NativeWindowHandle : null);

        return new VisualElementQueryResult(this, snapshot, fields, missingFields, null);
    }

    /// <inheritdoc />
    protected override IVisualElementEnumerator CreateEnumeratorCore(
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
        => new ScenarioMockQueryEnumerator(this, relation, options);

    /// <inheritdoc />
    protected override void InvokeCore()
    {
        if (!TryGetControl(out _))
        {
            throw new InvalidOperationException($"Element '{Id}' is no longer available.");
        }

        // TODO: Add deterministic per-element invocation callbacks shared with the controlled TestApps; see 07-Migration section 4.17.
        Backend.Operations.ActionInvoked();
    }

    /// <inheritdoc />
    protected override void SetTextCore(string text)
    {
        if (!TryGetControl(out var control))
        {
            throw new InvalidOperationException($"Element '{Id}' is no longer available.");
        }

        if ((control.States & ScenarioControlStates.Disabled) != 0)
        {
            throw new InvalidOperationException($"Element '{Id}' is disabled and cannot accept text.");
        }

        if ((control.States & ScenarioControlStates.ReadOnly) != 0)
        {
            throw new InvalidOperationException($"Element '{Id}' is read-only and cannot accept text.");
        }

        if (control.Kind is not ScenarioControlKind.TextBox and not ScenarioControlKind.Document)
        {
            throw new NotSupportedException($"Element '{Id}' does not expose editable scalar text.");
        }

        Backend.SetText(this, text);
    }

    /// <inheritdoc />
    protected override void FocusCore()
    {
        if (!TryGetControl(out _))
        {
            throw new InvalidOperationException($"Element '{Id}' is no longer available.");
        }

        Backend.Operations.ActionInvoked();
    }

    /// <inheritdoc />
    protected override void SendKeyGestureCore(KeyGesture keyGesture)
    {
        if (keyGesture.Key == Key.None)
        {
            throw new NotSupportedException("A keyboard gesture must contain a key.");
        }

        Backend.Operations.ActionInvoked();
    }

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IVisualElementCapture>(new NotSupportedException("The mock visual backend does not provide bitmap captures."));
    }

    /// <inheritdoc />
    protected override void ReleaseCore() => Backend.Operations.ElementReleased();

    internal bool TryGetControl([NotNullWhen(true)] out VisualControl? control)
    {
        try
        {
            control = Backend.Resolve(controlResolver());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            control = null;
            return false;
        }
    }

    internal static VisualElementStates MapStates(ScenarioControlStates states) => (VisualElementStates)(int)states;

    internal static VisualElementType MapType(ScenarioControlKind kind) => kind switch
    {
        ScenarioControlKind.Window or ScenarioControlKind.Dialog => VisualElementType.TopLevel,
        ScenarioControlKind.Panel or ScenarioControlKind.VerticalStack or
            ScenarioControlKind.HorizontalStack or ScenarioControlKind.Group => VisualElementType.Panel,
        ScenarioControlKind.Text => VisualElementType.Label,
        ScenarioControlKind.FragmentedText or ScenarioControlKind.Document => VisualElementType.Document,
        ScenarioControlKind.Image => VisualElementType.Image,
        ScenarioControlKind.Button => VisualElementType.Button,
        ScenarioControlKind.Link => VisualElementType.Hyperlink,
        ScenarioControlKind.TextBox => VisualElementType.TextEdit,
        ScenarioControlKind.CheckBox => VisualElementType.CheckBox,
        ScenarioControlKind.RadioButton => VisualElementType.RadioButton,
        ScenarioControlKind.ComboBox => VisualElementType.ComboBox,
        ScenarioControlKind.Slider => VisualElementType.Slider,
        ScenarioControlKind.ProgressBar => VisualElementType.ProgressBar,
        ScenarioControlKind.List or ScenarioControlKind.VirtualList => VisualElementType.ListView,
        ScenarioControlKind.Tree => VisualElementType.TreeView,
        ScenarioControlKind.Table => VisualElementType.Table,
        ScenarioControlKind.TabControl => VisualElementType.TabControl,
        ScenarioControlKind.TabItem => VisualElementType.TabItem,
        ScenarioControlKind.MenuBar => VisualElementType.Menu,
        ScenarioControlKind.MenuItem => VisualElementType.MenuItem,
        ScenarioControlKind.Separator => VisualElementType.Unknown,
        ScenarioControlKind.ToolBar => VisualElementType.ToolBar,
        ScenarioControlKind.StatusBar => VisualElementType.StatusBar,
        _ => VisualElementType.Unknown,
    };

    private VisualElementQueryResult CreateFailure(VisualElementFields missingFields, VisualElementQueryFailure failure) =>
        new(this, new VisualElementSnapshot(null, null, null, null, null, false, null, null, null), VisualElementFields.None, missingFields, failure);

    private static string? GetBoundedText(string? text, int maxLength) =>
        text is { Length: var length } && length > maxLength ? text[..maxLength] : text;

    private static bool HasField(VisualElementFields fields, VisualElementFields field) => (fields & field) != 0;

    private int GetDepth()
    {
        var depth = 0;
        var parent = ParentElement;
        while (parent is not null)
        {
            depth++;
            parent = parent.ParentElement;
        }

        return depth;
    }
}
