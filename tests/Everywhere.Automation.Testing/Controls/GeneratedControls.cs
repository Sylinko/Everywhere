namespace Everywhere.Automation.Testing;

/// <summary>
/// Declares a lazily indexed list whose logical size may be much larger than the observed portion.
/// </summary>
public sealed class VirtualList : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.VirtualList;

    /// <inheritdoc />
    public override int ChildCount { get; }

    private readonly ScenarioContext _itemContext;
    private readonly Func<ScenarioContext, int, VisualControl> _itemFactory;

    /// <summary>
    /// Initializes a virtual list without creating any item controls.
    /// </summary>
    /// <param name="context">The context used to derive stable item paths.</param>
    /// <param name="key">The scenario-local key of the list.</param>
    /// <param name="count">The logical item count.</param>
    /// <param name="itemFactory">Creates one item for its stable indexed context.</param>
    public VirtualList(
        ScenarioContext context,
        string key,
        int count,
        Func<ScenarioContext, int, VisualControl> itemFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Key = key;
        ChildCount = count;
        _itemContext = context.For(key);
        _itemFactory = itemFactory;
    }

    /// <inheritdoc />
    public override VisualControl GetChild(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ChildCount);
        return _itemFactory(_itemContext.For(index), index);
    }
}

/// <summary>
/// Repeats a lazily generated control template a fixed number of times.
/// </summary>
public sealed class Repeat : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Group;

    /// <inheritdoc />
    public override int ChildCount { get; }

    private readonly ScenarioContext _itemContext;
    private readonly Func<ScenarioContext, int, VisualControl> _itemFactory;

    /// <summary>
    /// Initializes a lazy repeated-control group.
    /// </summary>
    public Repeat(
        ScenarioContext context,
        string key,
        int count,
        Func<ScenarioContext, int, VisualControl> itemFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Key = key;
        ChildCount = count;
        _itemContext = context.For(key);
        _itemFactory = itemFactory;
    }

    /// <inheritdoc />
    public override VisualControl GetChild(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ChildCount);
        return _itemFactory(_itemContext.For(index), index);
    }
}

/// <summary>
/// Splits one logical text value into multiple child text controls to model fragmented accessibility trees.
/// </summary>
public sealed class FragmentedText : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.FragmentedText;

    /// <inheritdoc />
    public override int ChildCount { get; }

    private readonly string _text;

    /// <summary>
    /// Initializes fragmented text with at most the requested number of non-empty fragments.
    /// </summary>
    public FragmentedText(string text, int fragments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fragments);

        _text = text;
        ChildCount = Math.Min(text.Length, fragments);
    }

    /// <inheritdoc />
    public override VisualControl GetChild(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ChildCount);

        var start = (int)((long)_text.Length * index / ChildCount);
        var end = (int)((long)_text.Length * (index + 1) / ChildCount);
        return new Text(_text[start..end]);
    }
}

/// <summary>
/// Resolves to a deterministic control state selected by the backend's current MoveNext step.
/// </summary>
public sealed class OnMoveNext : VisualControl
{
    /// <inheritdoc />
    public override ScenarioControlKind Kind => ScenarioControlKind.Mutation;

    private readonly Func<long, VisualControl> _stateFactory;

    /// <summary>
    /// Initializes a mutable control from a deterministic step factory.
    /// </summary>
    public OnMoveNext(Func<long, VisualControl> stateFactory)
    {
        _stateFactory = stateFactory;
    }

    /// <summary>
    /// Resolves the logical control state for the specified non-negative MoveNext step.
    /// </summary>
    public VisualControl Resolve(long step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        return _stateFactory(step);
    }
}
