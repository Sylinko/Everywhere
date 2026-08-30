using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Automation;

/// <summary>
/// Maintains the one retained managed <see cref="VisualElement" /> incarnation for each platform identity within a <see cref="VisualContext" />.
/// </summary>
/// <typeparam name="TIdentity">The immutable backend-qualified identity type.</typeparam>
/// <remarks>
/// The map canonicalizes identity but does not independently retain elements. An entry exists exactly while at least one <see cref="VisualElementRetention" /> owns it.
/// </remarks>
public sealed class VisualElementIdentityMap<TIdentity> where TIdentity : notnull
{
    private readonly VisualContext _context;
    private readonly Dictionary<TIdentity, VisualElementIdentityEntry<TIdentity>> _entries;

    internal VisualElementIdentityMap(VisualContext context, IEqualityComparer<TIdentity>? comparer)
    {
        _context = context;
        _entries = new Dictionary<TIdentity, VisualElementIdentityEntry<TIdentity>>(comparer ?? EqualityComparer<TIdentity>.Default);
    }

    /// <summary>
    /// Gets the canonical element for an identity and retains it for the supplied logical owner.
    /// </summary>
    /// <typeparam name="TElement">The concrete element implementation.</typeparam>
    /// <typeparam name="TState">The state passed to the candidate factory without a closure allocation.</typeparam>
    /// <param name="retention">The owner that receives the canonical element before it is returned.</param>
    /// <param name="identity">The backend-qualified platform identity.</param>
    /// <param name="state">The state passed to <paramref name="factory" />.</param>
    /// <param name="factory">Creates an unretained candidate when no canonical element exists.</param>
    /// <returns>The retained canonical element.</returns>
    public TElement GetOrAdd<TElement, TState>(
        VisualElementRetention retention,
        TIdentity identity,
        TState state,
        Func<TIdentity, TState, TElement> factory) where TElement : VisualElement
    {
        _context.ValidateRetention(retention);
        if (_entries.TryGetValue(identity, out var existingEntry))
        {
            var existingElement = GetCompatibleElement<TElement>(existingEntry.Element);
            retention.Retain(existingEntry);
            return existingElement;
        }

        var candidate = factory(identity, state);
        try
        {
            _context.ValidateIdentityCandidate(candidate);
            var entry = new VisualElementIdentityEntry<TIdentity>(this, identity, candidate);
            candidate.AttachIdentity(entry);
            _entries.Add(identity, entry);
            retention.Retain(entry);
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
        _context.ValidateRetention(retention);
        if (!_entries.TryGetValue(identity, out var entry))
        {
            element = null;
            return false;
        }

        element = GetCompatibleElement<TElement>(entry.Element);
        retention.Retain(entry);
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
        _context.ValidateRetention(retention);
        var lookup = _entries.GetAlternateLookup<TAlternateIdentity>();
        if (!lookup.TryGetValue(identity, out var entry))
        {
            element = null;
            return false;
        }

        element = GetCompatibleElement<TElement>(entry.Element);
        retention.Retain(entry);
        return true;
    }

    /// <summary>
    /// Materializes a durable identity from an alternate representation through this map's comparer.
    /// </summary>
    public TIdentity CreateIdentity<TAlternateIdentity>(TAlternateIdentity identity) where TAlternateIdentity : notnull, allows ref struct
    {
        _context.ThrowIfDisposed();
        var comparer = _entries.Comparer as IAlternateEqualityComparer<TAlternateIdentity, TIdentity> ??
            throw new InvalidOperationException($"The identity comparer does not support alternate keys of type {typeof(TAlternateIdentity)}.");
        return comparer.Create(identity);
    }

    internal void Remove(VisualElementIdentityEntry<TIdentity> entry) =>
        ((ICollection<KeyValuePair<TIdentity, VisualElementIdentityEntry<TIdentity>>>)_entries).Remove(
            new KeyValuePair<TIdentity, VisualElementIdentityEntry<TIdentity>>(entry.Identity, entry));

    private static TElement GetCompatibleElement<TElement>(VisualElement element) where TElement : VisualElement =>
        element as TElement ?? throw new InvalidOperationException("One platform identity resolved to incompatible VisualElement implementations.");
}

internal abstract class VisualElementIdentityEntry(VisualElement element)
{
    internal VisualElement Element { get; } = element;

    internal int RetainerCount { get; set; }

    internal abstract void RemoveFromMap();
}

internal sealed class VisualElementIdentityEntry<TIdentity>(VisualElementIdentityMap<TIdentity> map, TIdentity identity, VisualElement element)
    : VisualElementIdentityEntry(element) where TIdentity : notnull
{
    internal TIdentity Identity { get; } = identity;

    internal override void RemoveFromMap() => map.Remove(this);
}