using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Everywhere.AI;

/// <summary>
/// An immutable, ordered model catalog revision. The key identifies a model while definition
/// equality determines whether an existing instance can be retained across revisions.
/// </summary>
public sealed class ModelCatalogSnapshot<TDefinition, TKey>
    where TDefinition : class
    where TKey : notnull
{
    public IReadOnlyList<TDefinition> Definitions { get; }

    public static ModelCatalogSnapshot<TDefinition, TKey> Empty { get; } =
        new(ImmutableArray<TDefinition>.Empty, FrozenDictionary<TKey, TDefinition>.Empty);

    private readonly IReadOnlyDictionary<TKey, TDefinition> _definitionsByKey;

    private ModelCatalogSnapshot(
        IReadOnlyList<TDefinition> definitions,
        IReadOnlyDictionary<TKey, TDefinition> definitionsByKey)
    {
        Definitions = definitions;
        _definitionsByKey = definitionsByKey;
    }

    public static ModelCatalogSnapshot<TDefinition, TKey> Create(
        IEnumerable<KeyValuePair<TKey, TDefinition>> definitions,
        ModelCatalogSnapshot<TDefinition, TKey>? previous = null)
    {
        var ordered = new List<TDefinition>();
        var orderedKeys = new List<TKey>();
        var byKey = new Dictionary<TKey, TDefinition>();
        foreach (var (key, candidate) in definitions)
        {
            var definition = previous is not null &&
                previous.TryGetDefinition(key, out var current) &&
                EqualityComparer<TDefinition>.Default.Equals(current, candidate) ?
                    current :
                    candidate;
            byKey.Add(key, definition);
            orderedKeys.Add(key);
            ordered.Add(definition);
        }

        if (previous is not null && previous.Definitions.Count == ordered.Count)
        {
            var unchanged = true;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ReferenceEquals(previous.Definitions[i], ordered[i]) &&
                    previous.TryGetDefinition(orderedKeys[i], out var previousDefinition) &&
                    ReferenceEquals(previousDefinition, ordered[i])) continue;
                unchanged = false;
                break;
            }
            if (unchanged) return previous;
        }

        return new ModelCatalogSnapshot<TDefinition, TKey>(ordered.ToImmutableArray(), byKey.ToFrozenDictionary());
    }

    public bool TryGetDefinition(TKey key, [NotNullWhen(true)] out TDefinition? definition) =>
        _definitionsByKey.TryGetValue(key, out definition);
}