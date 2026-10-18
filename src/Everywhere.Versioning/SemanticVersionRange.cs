using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Common;

/// <summary>
/// Represents a non-floating interval using Everywhere version precedence. A bare version is an
/// inclusive minimum; brackets around a single version mean exact comparison identity.
/// </summary>
public sealed class SemanticVersionRange
{
    /// <summary>
    /// Gets the lower bound, or null when unbounded below.
    /// </summary>
    public SemanticVersion? MinVersion { get; }

    /// <summary>
    /// Gets the upper bound, or null when unbounded above.
    /// </summary>
    public SemanticVersion? MaxVersion { get; }

    /// <summary>
    /// Gets whether the lower bound belongs to this interval.
    /// </summary>
    public bool IsMinInclusive { get; }

    /// <summary>
    /// Gets whether the upper bound belongs to this interval.
    /// </summary>
    public bool IsMaxInclusive { get; }

    /// <summary>
    /// Stores bounds validated by the parser.
    /// </summary>
    private SemanticVersionRange(SemanticVersion? minVersion, SemanticVersion? maxVersion, bool isMinInclusive, bool isMaxInclusive)
    {
        MinVersion = minVersion;
        MaxVersion = maxVersion;
        IsMinInclusive = isMinInclusive;
        IsMaxInclusive = isMaxInclusive;
    }

    /// <summary>
    /// Parses exact, open, closed and one-sided ranges. Empty declarations and floating versions
    /// are invalid; the explicit interval (,) accepts every version.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SemanticVersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim();
        if (SemanticVersion.TryParse(text, out var minimum))
        {
            range = new SemanticVersionRange(minimum, null, true, false);
            return true;
        }

        if (text.Length < 2 || text[0] is not ('[' or '(') || text[^1] is not (']' or ')')) return false;

        var isMinInclusive = text[0] == '[';
        var isMaxInclusive = text[^1] == ']';
        var bounds = text[1..^1].Split(',');
        if (bounds.Length == 1)
        {
            if (!isMinInclusive || !isMaxInclusive || !SemanticVersion.TryParse(bounds[0].Trim(), out var exact)) return false;
            range = new SemanticVersionRange(exact, exact, true, true);
            return true;
        }

        if (bounds.Length != 2) return false;
        var lower = bounds[0].Trim();
        var upper = bounds[1].Trim();
        var maximum = default(SemanticVersion);
        if (lower.Length != 0 && !SemanticVersion.TryParse(lower, out minimum)) return false;
        if (upper.Length != 0 && !SemanticVersion.TryParse(upper, out maximum)) return false;
        if (minimum is null && isMinInclusive || maximum is null && isMaxInclusive) return false;

        if (minimum is not null && maximum is not null)
        {
            var comparison = minimum.CompareTo(maximum);
            if (comparison > 0 || comparison == 0 && (!isMinInclusive || !isMaxInclusive)) return false;
        }

        range = new SemanticVersionRange(minimum, maximum, isMinInclusive, isMaxInclusive);
        return true;
    }

    /// <summary>
    /// Parses an interval or throws when its declaration is invalid.
    /// </summary>
    public static SemanticVersionRange Parse(string input)
    {
        return TryParse(input, out var range) ? range : throw new FormatException($"Invalid version range '{input}'.");
    }

    /// <summary>
    /// Checks both bounds using version precedence without an additional prerelease or channel filter.
    /// </summary>
    public bool Contains(SemanticVersion version)
    {
        if (MinVersion is not null)
        {
            var comparison = version.CompareTo(MinVersion);
            if (comparison < 0 || comparison == 0 && !IsMinInclusive) return false;
        }

        if (MaxVersion is not null)
        {
            var comparison = version.CompareTo(MaxVersion);
            if (comparison > 0 || comparison == 0 && !IsMaxInclusive) return false;
        }

        return true;
    }

    /// <summary>
    /// Formats canonical bounds, using the exact form when both bounds identify the same version.
    /// </summary>
    public override string ToString()
    {
        if (MinVersion is not null && MinVersion == MaxVersion) return $"[{MinVersion.ToNormalizedString()}]";
        return $"{(IsMinInclusive ? '[' : '(')}{MinVersion?.ToNormalizedString()},{MaxVersion?.ToNormalizedString()}{(IsMaxInclusive ? ']' : ')')}";
    }
}