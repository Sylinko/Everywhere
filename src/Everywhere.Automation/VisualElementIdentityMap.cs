using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Automation;

/// <summary>
/// Creates one unretained visual element with its immutable Context identity.
/// </summary>
/// <typeparam name="TIdentity">The immutable backend-qualified identity type.</typeparam>
/// <typeparam name="TState">The factory state type.</typeparam>
/// <typeparam name="TElement">The concrete visual element type.</typeparam>
/// <param name="identity">The identity that the new element must receive during construction.</param>
/// <param name="state">The caller-supplied factory state.</param>
/// <returns>The constructed unretained element.</returns>
public delegate TElement VisualElementFactory<TIdentity, in TState, out TElement>(VisualElementIdentity<TIdentity> identity, TState state)
    where TIdentity : notnull
    where TState : allows ref struct
    where TElement : VisualElement;

/// <summary>
/// Maintains the one retained managed <see cref="VisualElement" /> incarnation for each platform identity within a <see cref="VisualContext" />.
/// </summary>
/// <typeparam name="TIdentity">The immutable backend-qualified identity type.</typeparam>
/// <remarks>
/// The map canonicalizes identity but does not independently retain elements. An element remains mapped exactly while at least one <see cref="VisualElementRetention" /> owns it.
/// </remarks>
public sealed class VisualElementIdentityMap<TIdentity>(VisualContext context, IEqualityComparer<TIdentity>? comparer) where TIdentity : notnull
{
    public VisualContext Context { get; } = context;

    private readonly Dictionary<TIdentity, VisualElement> _elements = new(comparer ?? EqualityComparer<TIdentity>.Default);

    /// <summary>
    /// Gets the canonical element for an identity and retains it for the supplied logical owner.
    /// </summary>
    /// <typeparam name="TElement">The concrete element implementation.</typeparam>
    /// <typeparam name="TState">The state passed to the candidate factory without a closure allocation.</typeparam>
    /// <param name="retention">The owner that receives the canonical element before it is returned.</param>
    /// <param name="identity">The backend-qualified platform identity.</param>
    /// <param name="state">The state passed to <paramref name="factory" />.</param>
    /// <param name="factory">Creates an unretained element with the supplied immutable identity when no canonical element exists.</param>
    /// <returns>The retained canonical element.</returns>
    public TElement GetOrAdd<TElement, TState>(
        VisualElementRetention retention,
        TIdentity identity,
        TState state,
        VisualElementFactory<TIdentity, TState, TElement> factory)
        where TElement : VisualElement
        where TState : allows ref struct
    {
        Context.ValidateRetention(retention);
        if (_elements.TryGetValue(identity, out var existingElement))
        {
            var compatibleElement = GetCompatibleElement<TElement>(existingElement);
            retention.RetainCanonical(compatibleElement);
            return compatibleElement;
        }

        var elementIdentity = new VisualElementIdentity<TIdentity>(this, identity);
        var candidate = factory(elementIdentity, state);
        try
        {
            Debug.Assert(ReferenceEquals(candidate.Identity, elementIdentity));
            _elements.Add(identity, candidate);
            retention.RetainCanonical(candidate);
            return candidate;
        }
        catch
        {
            candidate.ReleaseUnretained();
            throw;
        }
    }

    /// <summary>
    /// Attempts to retain an existing canonical element by its durable identity.
    /// </summary>
    public bool TryGet<TElement>(VisualElementRetention retention, TIdentity identity, [NotNullWhen(true)] out TElement? element)
        where TElement : VisualElement
    {
        Context.ValidateRetention(retention);
        if (!_elements.TryGetValue(identity, out var existingElement))
        {
            element = null;
            return false;
        }

        element = GetCompatibleElement<TElement>(existingElement);
        retention.RetainCanonical(element);
        return true;
    }

    /// <summary>
    /// Attempts to retain an existing canonical element through an allocation-free alternate identity representation.
    /// </summary>
    public bool TryGetAlternate<TAlternateIdentity, TElement>(
        VisualElementRetention retention,
        TAlternateIdentity identity,
        [NotNullWhen(true)] out TElement? element)
        where TAlternateIdentity : notnull, allows ref struct
        where TElement : VisualElement
    {
        Context.ValidateRetention(retention);
        var lookup = _elements.GetAlternateLookup<TAlternateIdentity>();
        if (!lookup.TryGetValue(identity, out var existingElement))
        {
            element = null;
            return false;
        }

        element = GetCompatibleElement<TElement>(existingElement);
        retention.RetainCanonical(element);
        return true;
    }

    /// <summary>
    /// Materializes a durable identity from an alternate representation through this map's comparer.
    /// </summary>
    public TIdentity CreateIdentity<TAlternateIdentity>(TAlternateIdentity identity) where TAlternateIdentity : notnull, allows ref struct
    {
        Context.ThrowIfDisposed();
        var comparer = _elements.Comparer as IAlternateEqualityComparer<TAlternateIdentity, TIdentity> ??
            throw new InvalidOperationException($"The identity comparer does not support alternate keys of type {typeof(TAlternateIdentity)}.");
        return comparer.Create(identity);
    }

    internal void Remove(TIdentity identity, VisualElement element) =>
        ((ICollection<KeyValuePair<TIdentity, VisualElement>>)_elements).Remove(new KeyValuePair<TIdentity, VisualElement>(identity, element));

    private static TElement GetCompatibleElement<TElement>(VisualElement element) where TElement : VisualElement =>
        element as TElement ?? throw new InvalidOperationException("One platform identity resolved to incompatible VisualElement implementations.");
}

/// <summary>
/// Binds a visual element to one immutable backend identity in its owning Context.
/// </summary>
public abstract class VisualElementIdentity
{
    public VisualContext Context { get; }

    public int RetainerCount { get; internal set; }

    private protected VisualElementIdentity(VisualContext context) => Context = context;

    internal abstract void RemoveFromMap(VisualElement element);
}

/// <summary>
/// Binds a visual element to one immutable typed backend identity in its owning Context.
/// </summary>
/// <typeparam name="TIdentity">The immutable backend-qualified identity type.</typeparam>
public sealed class VisualElementIdentity<TIdentity> : VisualElementIdentity where TIdentity : notnull
{
    /// <summary>
    /// Gets the immutable backend-qualified identity value.
    /// </summary>
    public TIdentity Value { get; }

    private readonly VisualElementIdentityMap<TIdentity> _map;

    internal VisualElementIdentity(VisualElementIdentityMap<TIdentity> map, TIdentity value) : base(map.Context)
    {
        _map = map;
        Value = value;
    }

    internal override void RemoveFromMap(VisualElement element) => _map.Remove(Value, element);
}