namespace Everywhere.Utilities;

/// <summary>
/// A utility class for deduplicating names by appending a numbered suffix if necessary.
/// </summary>
public sealed class NumberedDeduplicator
{
    private readonly HashSet<string> _namePool = [];

    public string Deduplicate(string name) => Deduplicate(name, int.MaxValue);

    /// <summary>
    /// Reserves a unique name within <paramref name="maxLength"/> characters, truncating the stem
    /// when necessary so the final name (including any numeric suffix) never exceeds the limit.
    /// </summary>
    public string Deduplicate(string name, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 4);

        var deduplicatedName = name.Length > maxLength ? name[..maxLength] : name;
        var suffix = 1;
        while (_namePool.Contains(deduplicatedName))
        {
            var suffixText = $"_{suffix++}";
            if (suffixText.Length >= maxLength)
            {
                throw new InvalidOperationException("Deduplication suffix exceeds maximum allowed length.");
            }

            var maxStemLength = maxLength - suffixText.Length;
            var stem = name.Length > maxStemLength ? name.AsSpan(0, maxStemLength) : name.AsSpan();
            deduplicatedName = string.Concat(stem, suffixText);
        }

        _namePool.Add(deduplicatedName);
        return deduplicatedName;
    }
}
