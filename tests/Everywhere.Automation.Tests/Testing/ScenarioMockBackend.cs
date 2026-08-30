using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Everywhere.I18N;
using Everywhere.Automation;
using Everywhere.Automation.Testing;
using VisualContextLocaleKey = Everywhere.Automation.I18N.LocaleKey;
using LegacyCapturedBitmapData = Everywhere.Interop.IVisualElement.ICapturedBitmapData;
using LegacyKeyboardShortcut = Everywhere.Interop.KeyboardShortcut;
using LegacyVisualElement = Everywhere.Interop.IVisualElement;
using LegacyVisualElementSiblingAccessor = Everywhere.Interop.VisualElementSiblingAccessor;
using LegacyVisualElementStates = Everywhere.Interop.VisualElementStates;
using LegacyVisualElementType = Everywhere.Interop.VisualElementType;
using LiveVisualContext = Everywhere.Automation.VisualContext;

namespace Everywhere.Core.Tests.Chat.VisualContext.Testing;

internal sealed class ScenarioMockBackend : VisualContextRuntime
{
    public LiveVisualContext Context { get; }

    public ScenarioMockOperations Operations { get; } = new();

    public VisualContextPlatformOptions? LastPlatformOptions { get; private set; }

    public ScenarioVisualElement RootElement => _roots.Count == 1
        ? _roots[0]
        : throw new InvalidOperationException($"Scenario '{_scenario.Name}' contains {_roots.Count} roots.");

    public IReadOnlyList<ScenarioVisualElement> RootElements => _roots;

    public long Step => Interlocked.Read(ref _step);

    internal bool HasKnownCount { get; }

    internal VisualElementFields SupportedFields { get; }

    private readonly GeneratedVisualScenario _scenario;
    private readonly IReadOnlyList<ScenarioVisualElement> _roots;
    private readonly Dictionary<string, ScenarioVisualElement> _elements = [];
    private readonly Dictionary<string, string> _textOverrides = [];
    private readonly Func<string, VisualElementQueryFailure?>? _failureProvider;
    private long _step;

    public ScenarioMockBackend(
        GeneratedVisualScenario scenario,
        bool hasCount = true,
        VisualElementFields supportedFields = VisualElementFields.All,
        Func<string, VisualElementQueryFailure?>? failureProvider = null) : base(VisualContextRuntimeOptions.Default)
    {
        Context = new LiveVisualContext(this);
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
            var root = new ScenarioVisualElement(this, null, -1, CreateRootId(i), () => _scenario.Roots[rootIndex]);
            roots[i] = root;
            _elements.Add(root.Id, root);
            Operations.ElementCreated();
        }

        _roots = roots;
    }

    public VisualContextRuntimeLease CreateLease() => new ScenarioMockRuntimeLease(
        this,
        new VisualContextScopeDescriptor(0, TimeSpan.MaxValue),
        new VisualContextPlatformOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

    /// <inheritdoc />
    protected override ValueTask<VisualContextRuntimeLease> CreateScopeLeaseAsync(
        LiveVisualContext context,
        VisualContextScopeDescriptor scopeDescriptor,
        VisualContextPlatformOptions options)
    {
        scopeDescriptor.CancellationToken.ThrowIfCancellationRequested();
        LastPlatformOptions = options;
        return ValueTask.FromResult<VisualContextRuntimeLease>(new ScenarioMockRuntimeLease(this, scopeDescriptor, options));
    }

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

            var element = new ScenarioVisualElement(this, parent, index, id, () => parent.Control.GetChild(index));
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

    private string CreateRootId(int index) => _scenario.Roots.Count == 1
        ? $"{_scenario.Name}:{_scenario.Seed}"
        : $"{_scenario.Name}:{_scenario.Seed}/root/{index}";
}

internal sealed class ScenarioMockOperations
{
    public int RuntimeLeaseCreatedCount => Volatile.Read(ref _runtimeLeaseCreatedCount);

    public int RuntimeLeaseDisposedCount => Volatile.Read(ref _runtimeLeaseDisposedCount);

    public int ScalarQueryCount => Volatile.Read(ref _scalarQueryCount);

    public int MoveNextAttemptCount => Volatile.Read(ref _moveNextAttemptCount);

    public int EnumeratorCreatedCount => Volatile.Read(ref _enumeratorCreatedCount);

    public int EnumeratorDisposedCount => Volatile.Read(ref _enumeratorDisposedCount);

    public int ElementCreatedCount => Volatile.Read(ref _elementCreatedCount);

    public int ElementReleasedCount => Volatile.Read(ref _elementReleasedCount);

    public int ActionCount => Volatile.Read(ref _actionCount);

    private int _runtimeLeaseCreatedCount;
    private int _runtimeLeaseDisposedCount;
    private int _scalarQueryCount;
    private int _moveNextAttemptCount;
    private int _enumeratorCreatedCount;
    private int _enumeratorDisposedCount;
    private int _elementCreatedCount;
    private int _elementReleasedCount;
    private int _actionCount;

    internal void RuntimeLeaseCreated() => Interlocked.Increment(ref _runtimeLeaseCreatedCount);

    internal void RuntimeLeaseDisposed() => Interlocked.Increment(ref _runtimeLeaseDisposedCount);

    internal void ScalarQuery() => Interlocked.Increment(ref _scalarQueryCount);

    internal void MoveNextAttempted() => Interlocked.Increment(ref _moveNextAttemptCount);

    internal void EnumeratorCreated() => Interlocked.Increment(ref _enumeratorCreatedCount);

    internal void EnumeratorDisposed() => Interlocked.Increment(ref _enumeratorDisposedCount);

    internal void ElementCreated() => Interlocked.Increment(ref _elementCreatedCount);

    internal void ElementReleased() => Interlocked.Increment(ref _elementReleasedCount);

    internal void ActionInvoked() => Interlocked.Increment(ref _actionCount);
}

internal sealed class ScenarioMockRuntimeLease : VisualContextRuntimeLease
{
    private readonly ScenarioMockBackend _backend;
    private bool _isDisposed;

    public ScenarioMockRuntimeLease(ScenarioMockBackend backend, VisualContextScopeDescriptor scopeDescriptor, VisualContextPlatformOptions platformOptions) : base(backend.Context, scopeDescriptor, platformOptions)
    {
        _backend = backend;
        backend.Operations.RuntimeLeaseCreated();
    }

    /// <inheritdoc />
    public override ValueTask<VisualElementQueryResult?> QueryElementAsync(VisualElementLocator locator, VisualElementQueryRequest request) =>
        ValueTask.FromResult(QueryElement(locator, request));

    private VisualElementQueryResult? QueryElement(VisualElementLocator locator, VisualElementQueryRequest request)
    {
        ThrowIfUnavailable();
        var element = locator.Kind switch
        {
            VisualElementLocatorKind.Focused => _backend.RootElements[0],
            VisualElementLocatorKind.Point => _backend.RootElements.FirstOrDefault(root => root.BoundingRectangle.Contains(locator.Point)),
            VisualElementLocatorKind.NativeWindow => _backend.RootElements.FirstOrDefault(root => root.NativeWindowHandle == locator.NativeWindowHandle),
            _ => throw new ArgumentOutOfRangeException(nameof(locator), locator, null),
        };
        return element is null ? null : QueryElement(element, request);
    }

    /// <inheritdoc />
    public override ValueTask<VisualElementQueryResult> QueryElementAsync(VisualElement element, VisualElementQueryRequest request) =>
        ValueTask.FromResult(QueryElement(element, request));

    private VisualElementQueryResult QueryElement(VisualElement element, VisualElementQueryRequest request)
    {
        ThrowIfUnavailable();
        var scenarioElement = GetElement(element);
        _backend.Operations.ScalarQuery();

        var providerFailure = _backend.GetFailure(scenarioElement);
        if (providerFailure is not null)
        {
            return CreateFailure(scenarioElement, request.RequestedFields, providerFailure);
        }

        if (!scenarioElement.TryGetControl(out var control))
        {
            return CreateFailure(
                scenarioElement,
                request.RequestedFields,
                new VisualElementQueryFailure(
                    VisualElementQueryFailureKind.ElementUnavailable,
                    new DynamicLocaleKey(VisualContextLocaleKey.VisualContext_QueryFailure_ElementUnavailable)));
        }

        var fields = request.RequestedFields & _backend.SupportedFields;
        var missingFields = request.RequestedFields & ~fields;
        var text = HasField(fields, VisualElementFields.Text)
            ? GetBoundedText(_backend.GetText(scenarioElement, control), request.MaxTextCharacters)
            : null;
        var snapshot = new VisualElementSnapshot(
            HasField(fields, VisualElementFields.Id) ? scenarioElement.Id : null,
            HasField(fields, VisualElementFields.Type) ? ScenarioVisualElement.MapType(control.Kind) : null,
            HasField(fields, VisualElementFields.States) ? ScenarioVisualElement.MapStates(control.States) : null,
            HasField(fields, VisualElementFields.Name) ? control.Name : null,
            text,
            HasField(fields, VisualElementFields.Bounds) ? scenarioElement.BoundingRectangle : null,
            HasField(fields, VisualElementFields.ProcessId) ? scenarioElement.ProcessId : null,
            HasField(fields, VisualElementFields.NativeWindowHandle) ? scenarioElement.NativeWindowHandle : null);

        return new VisualElementQueryResult(
            scenarioElement,
            snapshot,
            fields,
            missingFields,
            null);
    }

    /// <inheritdoc />
    public override ValueTask<IVisualEnumerator<VisualElementQueryResult>> CreateEnumeratorAsync(
        VisualElement origin,
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
    {
        ThrowIfUnavailable();

        return ValueTask.FromResult<IVisualEnumerator<VisualElementQueryResult>>(new ScenarioMockQueryEnumerator(this, GetElement(origin), relation, options));
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _backend.Operations.RuntimeLeaseDisposed();
    }

    internal ValueTask<VisualElementQueryResult> QueryCurrentAsync(ScenarioVisualElement element, VisualElementQueryRequest request) =>
        QueryElementAsync(element, request);

    internal void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ScopeDescriptor.CancellationToken.ThrowIfCancellationRequested();
    }

    private ScenarioVisualElement GetElement(VisualElement element)
    {
        if (element is not ScenarioVisualElement scenarioElement || !ReferenceEquals(scenarioElement.Backend, _backend))
        {
            throw new ArgumentException("The element does not belong to this Runtime lease.", nameof(element));
        }

        return scenarioElement;
    }

    private static VisualElementQueryResult CreateFailure(
        ScenarioVisualElement element,
        VisualElementFields missingFields,
        VisualElementQueryFailure failure) =>
        new(
            element,
            new VisualElementSnapshot(null, null, null, null, null, null, null, null),
            VisualElementFields.None,
            missingFields,
            failure);

    private static string? GetBoundedText(string? text, int maxLength) =>
        text is { Length: var length } && length > maxLength ? text[..maxLength] : text;

    private static bool HasField(VisualElementFields fields, VisualElementFields field) => (fields & field) != 0;
}

internal sealed class ScenarioMockQueryEnumerator : IVisualEnumerator<VisualElementQueryResult>
{
    /// <inheritdoc />
    public VisualElementQueryResult Current
    {
        get
        {
            _runtimeLease.ThrowIfUnavailable();
            return _current ?? throw new InvalidOperationException("The enumerator has no current item.");
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            _runtimeLease.ThrowIfUnavailable();
            return _navigator.Count;
        }
    }

    /// <inheritdoc />
    public int Index
    {
        get
        {
            _runtimeLease.ThrowIfUnavailable();
            return _navigator.Index;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> HasMoreAsync()
    {
        _runtimeLease.ThrowIfUnavailable();
        return ValueTask.FromResult(_navigator.HasMore);
    }

    private readonly ScenarioMockRuntimeLease _runtimeLease;
    private readonly ScenarioRelationNavigator _navigator;
    private readonly VisualElementQueryRequest _queryRequest;
    private VisualElementQueryResult? _current;
    private bool _isDisposed;

    public ScenarioMockQueryEnumerator(
        ScenarioMockRuntimeLease runtimeLease,
        ScenarioVisualElement origin,
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
    {
        _runtimeLease = runtimeLease;
        _navigator = new ScenarioRelationNavigator(origin, relation);
        _queryRequest = options.QueryRequest;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _runtimeLease.ThrowIfUnavailable();
        if (!_navigator.MoveNext())
        {
            _current = null;
            return false;
        }

        _current = await _runtimeLease.QueryCurrentAsync(_navigator.Current, _queryRequest);
        return true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        _navigator.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScenarioRelationNavigator : IEnumerator<ScenarioVisualElement>
{
    /// <inheritdoc />
    public ScenarioVisualElement Current =>
        _current ?? throw new InvalidOperationException("The enumerator has no current item.");

    object IEnumerator.Current => Current;

    /// <inheritdoc />
    public int Count => _origin.Backend.HasKnownCount ? _initialCount : -1;

    /// <inheritdoc />
    public int Index { get; private set; } = -1;

    /// <inheritdoc />
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
        return parent is not null && parent.TryGetControl(out var control)
            ? Math.Max(0, control.ChildCount - _origin.SiblingIndex - 1)
            : 0;
    }

    private ScenarioVisualElement GetParent() =>
        _origin.ParentElement ?? throw new InvalidOperationException("The element has no parent.");
}

internal sealed class ScenarioVisualElement : VisualElement, LegacyVisualElement
{
    public LegacyVisualElement? Parent => ParentElement;

    public LegacyVisualElementSiblingAccessor SiblingAccessor => new ScenarioSiblingAccessor(this);

    public IEnumerable<LegacyVisualElement> Children => new ScenarioChildrenEnumerable(this);

    public LegacyVisualElementType Type => MapLegacyType(Control.Kind);

    public LegacyVisualElementStates States => MapLegacyStates(Control.States);

    public string? Name => Control.Name;

    public PixelRect BoundingRectangle => ParentElement is null
        ? new PixelRect(0, 0, 1280, 720)
        : new PixelRect(SiblingIndex * 8, GetDepth() * 24, 320, 20);

    public int ProcessId => 10_001;

    public nint NativeWindowHandle => 1;

    internal ScenarioMockBackend Backend { get; }

    internal ScenarioVisualElement? ParentElement { get; }

    internal int SiblingIndex { get; }

    internal VisualControl Control => TryGetControl(out var control)
        ? control
        : throw new InvalidOperationException($"Element '{Id}' is no longer available.");

    private readonly Func<VisualControl> _controlResolver;

    public ScenarioVisualElement(
        ScenarioMockBackend backend,
        ScenarioVisualElement? parent,
        int siblingIndex,
        string id,
        Func<VisualControl> controlResolver) : base(backend.Context, id)
    {
        Backend = backend;
        ParentElement = parent;
        SiblingIndex = siblingIndex;
        _controlResolver = controlResolver;
    }

    public string? GetText(int maxLength = -1)
    {
        var text = Backend.GetText(this, Control);
        return text is not null && maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
    }

    public string? GetSelectionText() => null;

    public void Invoke() => Backend.Operations.ActionInvoked();

    public void SetText(string text) => Backend.SetText(this, text);

    public void SendShortcut(LegacyKeyboardShortcut shortcut) => Backend.Operations.ActionInvoked();

    Task<LegacyCapturedBitmapData> LegacyVisualElement.CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromException<LegacyCapturedBitmapData>(
            new NotSupportedException("The mock visual backend does not provide bitmap captures."));

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) =>
        Task.FromException<IVisualElementCapture>(
            new NotSupportedException("The mock visual backend does not provide bitmap captures."));

    /// <inheritdoc />
    protected override void ReleaseCore() => Backend.Operations.ElementReleased();

    internal bool TryGetControl([NotNullWhen(true)] out VisualControl? control)
    {
        try
        {
            control = Backend.Resolve(_controlResolver());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            control = null;
            return false;
        }
    }

    internal static VisualElementStates MapStates(ScenarioControlStates states) => (VisualElementStates)(int)states;

    internal static LegacyVisualElementStates MapLegacyStates(ScenarioControlStates states) =>
        (LegacyVisualElementStates)(int)states;

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
        ScenarioControlKind.Mutation => VisualElementType.Unknown,
        _ => VisualElementType.Unknown,
    };

    internal static LegacyVisualElementType MapLegacyType(ScenarioControlKind kind) =>
        (LegacyVisualElementType)(int)MapType(kind);

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

internal sealed class ScenarioChildrenEnumerable(ScenarioVisualElement origin) : IEnumerable<LegacyVisualElement>
{
    public IEnumerator<LegacyVisualElement> GetEnumerator() =>
        new ScenarioLegacyEnumerator(origin, VisualElementRelation.Child);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ScenarioLegacyEnumerator : IEnumerator<LegacyVisualElement>
{
    public LegacyVisualElement Current => _navigator.Current;

    object IEnumerator.Current => Current;

    private readonly ScenarioRelationNavigator _navigator;

    public ScenarioLegacyEnumerator(ScenarioVisualElement origin, VisualElementRelation relation) =>
        _navigator = new ScenarioRelationNavigator(origin, relation);

    public bool MoveNext() => _navigator.MoveNext();

    public void Reset() => _navigator.Reset();

    public void Dispose() => _navigator.Dispose();
}

internal sealed class ScenarioSiblingAccessor(ScenarioVisualElement origin) : LegacyVisualElementSiblingAccessor
{
    protected override IEnumerator<LegacyVisualElement> CreateForwardEnumerator() =>
        new ScenarioLegacyEnumerator(origin, VisualElementRelation.NextSibling);

    protected override IEnumerator<LegacyVisualElement> CreateBackwardEnumerator() =>
        new ScenarioLegacyEnumerator(origin, VisualElementRelation.PreviousSibling);
}
