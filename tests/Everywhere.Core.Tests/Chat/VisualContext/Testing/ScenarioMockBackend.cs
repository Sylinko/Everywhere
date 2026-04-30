using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Everywhere.Interop;
using Everywhere.VisualContext.Testing;

namespace Everywhere.Core.Tests.Chat.VisualContext.Testing;

internal sealed class ScenarioMockBackend
{
    public ScenarioMockOperations Operations { get; } = new();

    public IVisualElement RootElement => _roots.Count == 1
        ? _roots[0]
        : throw new InvalidOperationException($"Scenario '{_scenario.Name}' contains {_roots.Count} roots.");

    public IReadOnlyList<IVisualElement> RootElements => _roots;

    public long Step => Interlocked.Read(ref _step);

    internal bool ExposesCounts { get; }

    internal VisualElementFields SupportedFields { get; }

    private readonly GeneratedVisualScenario _scenario;
    private readonly IReadOnlyList<ScenarioVisualElement> _roots;
    private readonly Dictionary<string, ScenarioVisualElement> _elements = [];
    private readonly Dictionary<string, string> _textOverrides = [];
    private readonly Func<string, VisualElementReadFailure?>? _failureProvider;
    private long _step;

    public ScenarioMockBackend(
        GeneratedVisualScenario scenario,
        bool exposesCounts = true,
        VisualElementFields supportedFields = VisualElementFields.All,
        Func<string, VisualElementReadFailure?>? failureProvider = null)
    {
        _scenario = scenario;
        ExposesCounts = exposesCounts;
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

    public IVisualElementReadSession CreateSession() => new ScenarioMockReadSession(this);

    public IVisualElement GetElement(params IReadOnlyList<int> path)
    {
        var current = _roots[0];
        foreach (var index in path)
        {
            current = GetChild(current, index);
        }

        return current;
    }

    public IVisualElement GetRoot(int index) => _roots[index];

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

    internal VisualElementReadFailure? GetFailure(ScenarioVisualElement element) => _failureProvider?.Invoke(element.Id);

    private string CreateRootId(int index) => _scenario.Roots.Count == 1
        ? $"{_scenario.Name}:{_scenario.Seed}"
        : $"{_scenario.Name}:{_scenario.Seed}/root/{index}";
}

internal sealed class ScenarioMockOperations
{
    public int ScalarReadCount => Volatile.Read(ref _scalarReadCount);

    public int MoveNextAttemptCount => Volatile.Read(ref _moveNextAttemptCount);

    public int EnumeratorCreatedCount => Volatile.Read(ref _enumeratorCreatedCount);

    public int EnumeratorDisposedCount => Volatile.Read(ref _enumeratorDisposedCount);

    public int ElementCreatedCount => Volatile.Read(ref _elementCreatedCount);

    public int ActionCount => Volatile.Read(ref _actionCount);

    private int _scalarReadCount;
    private int _moveNextAttemptCount;
    private int _enumeratorCreatedCount;
    private int _enumeratorDisposedCount;
    private int _elementCreatedCount;
    private int _actionCount;

    internal void ScalarRead() => Interlocked.Increment(ref _scalarReadCount);

    internal void MoveNextAttempted() => Interlocked.Increment(ref _moveNextAttemptCount);

    internal void EnumeratorCreated() => Interlocked.Increment(ref _enumeratorCreatedCount);

    internal void EnumeratorDisposed() => Interlocked.Increment(ref _enumeratorDisposedCount);

    internal void ElementCreated() => Interlocked.Increment(ref _elementCreatedCount);

    internal void ActionInvoked() => Interlocked.Increment(ref _actionCount);
}

internal sealed class ScenarioMockReadSession : IVisualElementReadSession
{
    private readonly ScenarioMockBackend _backend;
    private readonly HashSet<ScenarioMockReadEnumerator> _activeEnumerators = [];
    private bool _disposed;

    public ScenarioMockReadSession(ScenarioMockBackend backend) => _backend = backend;

    public VisualElementReadResult ReadElement(IVisualElement element, VisualElementReadRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scenarioElement = GetElement(element);
        _backend.Operations.ScalarRead();

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
                new VisualElementReadFailure(
                    VisualElementReadFailureKind.ElementUnavailable,
                    $"Element '{scenarioElement.Id}' is no longer available."));
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

        return new VisualElementReadResult(
            scenarioElement,
            snapshot,
            fields,
            missingFields,
            null);
    }

    public IVisualEnumerator<VisualElementReadResult> CreateEnumerator(
        IVisualElement origin,
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var enumerator = new ScenarioMockReadEnumerator(this, GetElement(origin), relation, options);
        lock (_activeEnumerators)
        {
            _activeEnumerators.Add(enumerator);
        }

        return enumerator;
    }

    public void Dispose()
    {
        ScenarioMockReadEnumerator[] activeEnumerators;
        lock (_activeEnumerators)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            activeEnumerators = [.. _activeEnumerators];
            _activeEnumerators.Clear();
        }

        foreach (var enumerator in activeEnumerators)
        {
            enumerator.Dispose();
        }
    }

    internal VisualElementReadResult ReadCurrent(ScenarioVisualElement element, VisualElementReadRequest request) =>
        ReadElement(element, request);

    internal void EnumeratorDisposed(ScenarioMockReadEnumerator enumerator)
    {
        lock (_activeEnumerators)
        {
            _activeEnumerators.Remove(enumerator);
        }
    }

    private ScenarioVisualElement GetElement(IVisualElement element)
    {
        if (element is not ScenarioVisualElement scenarioElement || !ReferenceEquals(scenarioElement.Backend, _backend))
        {
            throw new ArgumentException("The element does not belong to this read session.", nameof(element));
        }

        return scenarioElement;
    }

    private static VisualElementReadResult CreateFailure(
        ScenarioVisualElement element,
        VisualElementFields missingFields,
        VisualElementReadFailure failure) =>
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

internal sealed class ScenarioMockReadEnumerator : IVisualEnumerator<VisualElementReadResult>
{
    public VisualElementReadResult Current =>
        _current ?? throw new InvalidOperationException("The enumerator has no current item.");

    object IEnumerator.Current => Current;

    public bool HasCount => _navigator.HasCount;

    public int Count => _navigator.Count;

    public int Index => _navigator.Index;

    public bool HasMore => _navigator.HasMore;

    private readonly ScenarioMockReadSession _session;
    private readonly ScenarioRelationNavigator _navigator;
    private readonly VisualElementReadRequest _readRequest;
    private VisualElementReadResult? _current;
    private bool _disposed;

    public ScenarioMockReadEnumerator(
        ScenarioMockReadSession session,
        ScenarioVisualElement origin,
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
    {
        _session = session;
        _navigator = new ScenarioRelationNavigator(origin, relation);
        _readRequest = options.ReadRequest;
    }

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_navigator.MoveNext())
        {
            _current = null;
            return false;
        }

        _current = _session.ReadCurrent(_navigator.Current, _readRequest);
        return true;
    }

    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigator.Dispose();
        _session.EnumeratorDisposed(this);
    }
}

internal sealed class ScenarioRelationNavigator : IVisualEnumerator<ScenarioVisualElement>
{
    public ScenarioVisualElement Current =>
        _current ?? throw new InvalidOperationException("The enumerator has no current item.");

    object IEnumerator.Current => Current;

    public bool HasCount => _origin.Backend.ExposesCounts;

    public int Count => HasCount
        ? _initialCount
        : throw new NotSupportedException("This mock provider does not expose relation counts.");

    public int Index { get; private set; } = -1;

    public bool HasMore
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return !_completed && HasTarget(_nextOrdinal);
        }
    }

    private readonly ScenarioVisualElement _origin;
    private readonly VisualElementRelation _relation;
    private readonly int _initialCount;
    private ScenarioVisualElement? _current;
    private int _nextOrdinal;
    private bool _completed;
    private bool _disposed;

    public ScenarioRelationNavigator(ScenarioVisualElement origin, VisualElementRelation relation)
    {
        _origin = origin;
        _relation = relation;
        _initialCount = GetCount();
        origin.Backend.Operations.EnumeratorCreated();
    }

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _origin.Backend.AdvanceStep();

        if (_completed || !HasTarget(_nextOrdinal))
        {
            _completed = true;
            _current = null;
            Index = -1;
            return false;
        }

        _current = GetTarget(_nextOrdinal);
        Index = _nextOrdinal;
        _nextOrdinal++;
        return true;
    }

    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

internal sealed class ScenarioVisualElement : IVisualElement
{
    public string Id { get; }

    public IVisualElement? Parent => ParentElement;

    public VisualElementSiblingAccessor SiblingAccessor => new ScenarioSiblingAccessor(this);

    public IEnumerable<IVisualElement> Children => new ScenarioChildrenEnumerable(this);

    public VisualElementType Type => MapType(Control.Kind);

    public VisualElementStates States => MapStates(Control.States);

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
        Func<VisualControl> controlResolver)
    {
        Backend = backend;
        ParentElement = parent;
        SiblingIndex = siblingIndex;
        Id = id;
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

    public void SendShortcut(KeyboardShortcut shortcut) => Backend.Operations.ActionInvoked();

    public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromException<IVisualElement.ICapturedBitmapData>(
            new NotSupportedException("The mock visual backend does not provide bitmap captures."));

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

internal sealed class ScenarioChildrenEnumerable(ScenarioVisualElement origin) : IEnumerable<IVisualElement>
{
    public IEnumerator<IVisualElement> GetEnumerator() =>
        new ScenarioLegacyEnumerator(origin, VisualElementRelation.Child);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ScenarioLegacyEnumerator : IEnumerator<IVisualElement>
{
    public IVisualElement Current => _navigator.Current;

    object IEnumerator.Current => Current;

    private readonly ScenarioRelationNavigator _navigator;

    public ScenarioLegacyEnumerator(ScenarioVisualElement origin, VisualElementRelation relation) =>
        _navigator = new ScenarioRelationNavigator(origin, relation);

    public bool MoveNext() => _navigator.MoveNext();

    public void Reset() => _navigator.Reset();

    public void Dispose() => _navigator.Dispose();
}

internal sealed class ScenarioSiblingAccessor(ScenarioVisualElement origin) : VisualElementSiblingAccessor
{
    protected override IEnumerator<IVisualElement> CreateForwardEnumerator() =>
        new ScenarioLegacyEnumerator(origin, VisualElementRelation.NextSibling);

    protected override IEnumerator<IVisualElement> CreateBackwardEnumerator() =>
        new ScenarioLegacyEnumerator(origin, VisualElementRelation.PreviousSibling);
}
