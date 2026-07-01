namespace Everywhere.Utilities;

/// <summary>
/// A utility class for deduplicating names by appending a numbered suffix if necessary.
/// </summary>
public sealed class NumberedDeduplicator
{
    private readonly HashSet<string> _namePool = [];

    public string Deduplicate(string name)
    {
        var deduplicatedName = name;
        var suffix = 1;
        while (_namePool.Contains(deduplicatedName))
        {
            deduplicatedName = $"{name}_{suffix++}";
        }

        _namePool.Add(deduplicatedName);
        return deduplicatedName;
    }
}