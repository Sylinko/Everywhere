using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ZLinq;

namespace Everywhere.Common;

public sealed partial record SemanticVersion(
    int Major,
    int Minor = 0,
    int Build = 0,
    int Revision = 0,
    string? Suffix = null
) : IComparable, IComparable<SemanticVersion>, IComparable<Version>
{
    public UpdateChannel Channel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Suffix))
                return UpdateChannel.Stable;

            var span = Suffix.AsSpan();
            var dotIndex = span.IndexOf('.');

            var channelName = dotIndex >= 0 ? span[..dotIndex] : span;
            return Enum.TryParse<UpdateChannel>(channelName, ignoreCase: true, out var channel) ? channel : UpdateChannel.Unknown;
        }
    }

    public static bool TryCreate(int major, int minor, int build, int revision, string? suffix, out SemanticVersion? version)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = null;
        }

        if (major < 0 || minor < 0 || build < 0 || revision < 0 ||
            suffix is not null && (
                suffix.Length > 64 ||
                suffix.StartsWith('.') ||
                suffix.EndsWith('.') ||
                suffix.Contains("..", StringComparison.Ordinal) ||
                suffix.AsValueEnumerable().Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '.')))
        {
            version = null;
            return false;
        }

        version = new SemanticVersion(major, minor, build, revision, suffix);
        return true;
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out SemanticVersion? version)
    {
        if (input.IsNullOrWhiteSpace())
        {
            version = null;
            return false;
        }

        var match = ParseRegex().Match(input);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major))
        {
            version = null;
            return false;
        }

        var minor = 0;
        if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out minor))
        {
            version = null;
            return false;
        }

        var build = 0;
        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out build))
        {
            version = null;
            return false;
        }

        var revision = 0;
        if (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out revision))
        {
            version = null;
            return false;
        }

        var suffix = match.Groups[5].Success ? match.Groups[5].Value : null;
        return TryCreate(major, minor, build, revision, suffix, out version);
    }

    public static SemanticVersion? Parse(string input, SemanticVersion? defaultValue = null)
    {
        return TryParse(input, out var version) ? version : defaultValue;
    }

    /// <summary>
    /// Compares this instance to another SemanticVersion instance. The comparison is based on the Major, Minor, Build, Revision, and Suffix properties, in that order. The Suffix is compared based on the UpdateChannel it represents, and if both are unknown, they are compared as strings.
    /// </summary>
    /// <example>
    /// 1.6.0                     > 1.6.0-canary.999
    /// 1.6.0-canary.12           > 1.6.0-canary.2
    /// 1.6.0-canary.20260530     > 1.6.0-canary.20260529
    /// 1.6.0-canary.20260530.12  > 1.6.0-canary.20260530.2
    /// 1.6.0-canary.20260530.12  > 1.6.0-canary.20260530
    /// </example>
    /// <param name="other"></param>
    /// <returns></returns>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Build.CompareTo(other.Build);
        if (result != 0) return result;

        result = Revision.CompareTo(other.Revision);
        if (result != 0) return result;

        switch (Suffix)
        {
            case null when other.Suffix is null:
                return 0;
            case null:
                return 1;
            default:
                if (other.Suffix is null) return -1;
                break;
        }

        var channel = Channel;
        var otherChannel = other.Channel;

        // If both unknown, compare directly by suffix string (e.g. "beta" > "alpha" for OrdinalIgnoreCase)
        if (channel == UpdateChannel.Unknown && otherChannel == UpdateChannel.Unknown)
        {
            return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
        }

        // If not equal, just compare channel. If equal, compare suffix number (e.g. canary.12 > canary.2)
        if (channel != otherChannel)
        {
            return channel < otherChannel ? -1 : 1;
        }

        // canary.12 > canary.2
        // canary.20260530 > canary.20260529
        // canary.20260530.12 > canary.20260530.2
        return CompareChannelSuffix(Suffix.AsSpan(), other.Suffix.AsSpan());
    }

    private static int CompareChannelSuffix(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        left = GetChannelSuffixParts(left);
        right = GetChannelSuffixParts(right);

        while (true)
        {
            if (left.IsEmpty) return right.IsEmpty ? 0 : -1;
            if (right.IsEmpty) return 1;

            var leftDot = left.IndexOf('.');
            var rightDot = right.IndexOf('.');

            var leftPart = leftDot < 0 ? left : left[..leftDot];
            var rightPart = rightDot < 0 ? right : right[..rightDot];

            var leftNumeric = leftPart.AsValueEnumerable().All(static c => (uint)(c - '0') <= 9);
            var rightNumeric = rightPart.AsValueEnumerable().All(static c => (uint)(c - '0') <= 9);

            int result;
            if (leftNumeric && rightNumeric)
            {
                leftPart = leftPart.TrimStart('0');
                rightPart = rightPart.TrimStart('0');

                result = leftPart.Length != rightPart.Length ? leftPart.Length.CompareTo(rightPart.Length) : leftPart.SequenceCompareTo(rightPart);
            }
            else if (leftNumeric != rightNumeric)
            {
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = leftPart.CompareTo(rightPart, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0) return result < 0 ? -1 : 1;

            left = leftDot < 0 ? ReadOnlySpan<char>.Empty : left[(leftDot + 1)..];
            right = rightDot < 0 ? ReadOnlySpan<char>.Empty : right[(rightDot + 1)..];
        }
    }

    private static ReadOnlySpan<char> GetChannelSuffixParts(ReadOnlySpan<char> suffix)
    {
        var dotIndex = suffix.IndexOf('.');
        return dotIndex < 0 ? ReadOnlySpan<char>.Empty : suffix[(dotIndex + 1)..];
    }

    public int CompareTo(Version? other)
    {
        return other is null ? 1 : CompareTo((SemanticVersion)other);
    }

    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            SemanticVersion version => CompareTo(version),
            Version version => CompareTo(version),
            _ => throw new ArgumentException($"Object must be of type {nameof(SemanticVersion)} or {nameof(Version)}.", nameof(obj))
        };
    }

    public static implicit operator SemanticVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new SemanticVersion(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision)
        );
    }

    public static explicit operator Version(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new Version(version.Major, version.Minor, version.Build, version.Revision);
    }

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) => left is not null && left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion? left, SemanticVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;
    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;
    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => left is null || left.CompareTo(right) <= 0;

    public override string ToString()
    {
        var version = Revision == 0 ? $"{Major}.{Minor}.{Build}" : $"{Major}.{Minor}.{Build}.{Revision}";
        return Suffix.IsNullOrWhiteSpace() ? version : $"{version}-{Suffix}";
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+)(?:\.(\d+))?)?(?:-([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*))?$")]
    private static partial Regex ParseRegex();
}