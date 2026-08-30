using System.Globalization;

namespace Everywhere.Automation.Testing;

/// <summary>
/// Provides stable, path-addressed random values while a scenario is being declared.
/// </summary>
/// <remarks>
/// A value depends on the scenario seed, current path, and supplied key rather than call order.
/// This lets unrelated declarations be added without shifting every later generated value.
/// </remarks>
public sealed class ScenarioContext
{
    /// <summary>
    /// Gets the seed supplied for this generated scenario.
    /// </summary>
    public long Seed { get; }

    /// <summary>
    /// Gets the stable logical path represented by this context.
    /// </summary>
    public string Path { get; }

    internal ScenarioContext(long seed, string path)
    {
        Seed = seed;
        Path = path;
    }

    /// <summary>
    /// Creates a child context addressed by a stable textual segment.
    /// </summary>
    public ScenarioContext For(string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        return new ScenarioContext(Seed, $"{Path}/{segment}");
    }

    /// <summary>
    /// Creates a child context addressed by a zero-based logical index.
    /// </summary>
    public ScenarioContext For(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return For(index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Returns a stable integer in the specified half-open range.
    /// </summary>
    public int RandomInt(string key, int minInclusive, int maxExclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxExclusive, minInclusive);

        var range = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(StableScenarioRandom.GetValue(Seed, Path, key) % range);
    }

    /// <summary>
    /// Returns a stable Boolean value for the supplied key.
    /// </summary>
    public bool RandomBool(string key) => RandomInt(key, 0, 2) == 1;

    /// <summary>
    /// Generates a stable text value from the configured external text provider.
    /// </summary>
    public string RandomTextValue(string key, ScenarioTextKind kind) => ScenarioTextGenerator.Generate(this, key, kind);

    /// <summary>
    /// Declares a text control containing a stable generated value.
    /// </summary>
    public Text RandomText(string key, ScenarioTextKind kind) => new(RandomTextValue(key, kind)) { Key = key };

    internal int GetRandomizerSeed(string key) => unchecked((int)StableScenarioRandom.GetValue(Seed, Path, key));
}

internal static class StableScenarioRandom
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong GetValue(long seed, string path, string key)
    {
        var hash = OffsetBasis;
        AddUInt64(ref hash, unchecked((ulong)seed));
        AddString(ref hash, path);
        AddUInt64(ref hash, 0xff);
        AddString(ref hash, key);
        return Mix(hash);
    }

    private static void AddString(ref ulong hash, string value)
    {
        foreach (var character in value)
        {
            hash ^= character;
            hash *= Prime;
        }
    }

    private static void AddUInt64(ref ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
