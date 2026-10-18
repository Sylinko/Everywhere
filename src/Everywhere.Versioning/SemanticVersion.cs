using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Everywhere.Common;

/// <summary>
/// Represents a release version with optional prerelease labels and build metadata.
/// Comparison is independent of update channels. Build metadata does not affect precedence or equality.
/// Short numeric versions and a fourth revision component remain supported for existing Everywhere versions.
/// </summary>
/// <param name="Major">The major version.</param>
/// <param name="Minor">The minor version.</param>
/// <param name="Build">The patch version, retaining the existing property name.</param>
/// <param name="Revision">The optional fourth numeric component.</param>
/// <param name="Suffix">Dot-separated prerelease labels, without the leading hyphen.</param>
/// <param name="Metadata">Build metadata, without the leading plus sign.</param>
public sealed partial record SemanticVersion(
    int Major,
    int Minor = 0,
    int Build = 0,
    int Revision = 0,
    string? Suffix = null,
    string? Metadata = null
) : IComparable, IComparable<SemanticVersion>, IComparable<Version>
{
    /// <summary>
    /// Gets whether this version precedes the corresponding release without a suffix.
    /// </summary>
    public bool IsPrerelease => !string.IsNullOrEmpty(Suffix);

    /// <summary>
    /// Gets the recognized application feed without influencing comparison or validity.
    /// Unknown prerelease labels remain valid versions.
    /// </summary>
    public UpdateChannel Channel
    {
        get
        {
            if (!IsPrerelease) return UpdateChannel.Stable;

            var label = Suffix.AsSpan();
            var dotIndex = label.IndexOf('.');
            if (dotIndex >= 0) label = label[..dotIndex];

            return label.Equals("canary", StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Canary : UpdateChannel.Unknown;
        }
    }

    /// <summary>
    /// Validates numeric components and prerelease labels supplied at a parsing boundary.
    /// </summary>
    public static bool TryCreate(int major, int minor, int build, int revision, string? suffix, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(suffix)) suffix = null;
        if (major < 0 || minor < 0 || build < 0 || revision < 0 || suffix is not null && !LabelsRegex().IsMatch(suffix)) return false;

        version = new SemanticVersion(major, minor, build, revision, suffix);
        return true;
    }

    /// <summary>
    /// Parses one to four numeric components, optional prerelease labels and optional build metadata.
    /// Git tag prefixes belong to the caller and are not part of a version.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var match = ParseRegex().Match(input);
        if (!match.Success) return false;

        var components = new int[4];
        for (var index = 0; index < components.Length; index++)
        {
            var group = match.Groups[index + 1];
            if (group.Success && !int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out components[index])) return false;
        }

        version = new SemanticVersion(
            components[0],
            components[1],
            components[2],
            components[3],
            match.Groups[5].Success ? match.Groups[5].Value : null,
            match.Groups[6].Success ? match.Groups[6].Value : null);
        return true;
    }

    /// <summary>
    /// Parses a version, returning the supplied fallback when the input is invalid.
    /// </summary>
    public static SemanticVersion? Parse(string input, SemanticVersion? defaultValue = null)
    {
        return TryParse(input, out var version) ? version : defaultValue;
    }

    /// <summary>
    /// Compares numeric components followed by individual prerelease labels. Numeric labels compare
    /// numerically and precede text labels; text compares ordinally without case. A release follows
    /// all prereleases with the same numeric components. Metadata and channel recognition are ignored.
    /// </summary>
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

        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;

        return CompareLabels(Suffix.AsSpan(), other.Suffix.AsSpan());
    }

    /// <summary>
    /// Uses the same identity for equality, exact ranges and ordering, excluding build metadata.
    /// </summary>
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToNormalizedString());

    /// <inheritdoc />
    public int CompareTo(Version? other) => other is null ? 1 : CompareTo((SemanticVersion)other);

    /// <inheritdoc />
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

    /// <summary>
    /// Converts an assembly-style version, filling omitted components with zero.
    /// </summary>
    public static implicit operator SemanticVersion(Version version)
    {
        return new SemanticVersion(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    /// <summary>
    /// Extracts numeric components only; prerelease labels and metadata cannot be represented by Version.
    /// </summary>
    public static explicit operator Version(SemanticVersion version) => new(version.Major, version.Minor, version.Build, version.Revision);

    /// <summary>
    /// Compares versions with null preceding every version.
    /// </summary>
    public static bool operator >(SemanticVersion? left, SemanticVersion? right) => left is not null && left.CompareTo(right) > 0;

    /// <summary>
    /// Compares versions with null preceding every version.
    /// </summary>
    public static bool operator <(SemanticVersion? left, SemanticVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>
    /// Compares versions with null preceding every version.
    /// </summary>
    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;

    /// <summary>
    /// Compares versions with null preceding every version.
    /// </summary>
    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => left is null || left.CompareTo(right) <= 0;

    /// <summary>
    /// Formats the numeric version and preserves the original labels and metadata for display and persistence.
    /// </summary>
    public override string ToString()
    {
        var version = FormatNumbers();
        if (IsPrerelease) version.Append('-').Append(Suffix);
        if (!string.IsNullOrEmpty(Metadata)) version.Append('+').Append(Metadata);
        return version.ToString();
    }

    /// <summary>
    /// Formats the comparison identity: at least three numeric components, no zero revision,
    /// lowercase text labels, no numeric label padding, and no build metadata.
    /// </summary>
    public string ToNormalizedString()
    {
        var version = FormatNumbers();
        if (Suffix is not { Length: > 0 } suffix)
        {
            return version.ToString();
        }

        var separator = '-';
        foreach (var label in suffix.Split('.'))
        {
            version.Append(separator);
            if (IsNumeric(label.AsSpan()))
            {
                var number = label.AsSpan().TrimStart('0');
                if (number.IsEmpty)
                {
                    version.Append('0');
                }
                else
                {
                    version.Append(number);
                }
            }
            else
            {
                version.Append(label.ToLowerInvariant());
            }

            separator = '.';
        }

        return version.ToString();
    }

    /// <summary>
    /// Keeps persisted numeric formatting independent of the current culture.
    /// </summary>
    private StringBuilder FormatNumbers()
    {
        var version = new StringBuilder();
        version.Append(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Build}");
        if (Revision != 0)
        {
            version.Append(CultureInfo.InvariantCulture, $".{Revision}");
        }

        return version;
    }

    /// <summary>
    /// Compares numeric labels without integer conversion, including long build counters.
    /// </summary>
    private static int CompareLabels(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        while (true)
        {
            if (left.IsEmpty) return right.IsEmpty ? 0 : -1;
            if (right.IsEmpty) return 1;

            var leftDot = left.IndexOf('.');
            var rightDot = right.IndexOf('.');
            var leftPart = leftDot < 0 ? left : left[..leftDot];
            var rightPart = rightDot < 0 ? right : right[..rightDot];
            var isLeftNumeric = IsNumeric(leftPart);
            var isRightNumeric = IsNumeric(rightPart);
            if (isLeftNumeric != isRightNumeric) return isLeftNumeric ? -1 : 1;

            if (isLeftNumeric)
            {
                leftPart = leftPart.TrimStart('0');
                rightPart = rightPart.TrimStart('0');
                if (leftPart.Length != rightPart.Length) return leftPart.Length.CompareTo(rightPart.Length);
            }

            var result = leftPart.CompareTo(rightPart, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            left = leftDot < 0 ? ReadOnlySpan<char>.Empty : left[(leftDot + 1)..];
            right = rightDot < 0 ? ReadOnlySpan<char>.Empty : right[(rightDot + 1)..];
        }
    }

    /// <summary>
    /// Identifies ASCII decimal labels independently of the current culture.
    /// </summary>
    private static bool IsNumeric(ReadOnlySpan<char> label)
    {
        foreach (var character in label)
        {
            if (character is < '0' or > '9') return false;
        }

        return !label.IsEmpty;
    }

    /// <summary>
    /// Matches nonempty dot-separated ASCII version labels.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\z")]
    private static partial Regex LabelsRegex();

    /// <summary>
    /// Matches numeric components and separates prerelease labels from build metadata.
    /// </summary>
    [GeneratedRegex(
        @"\A([0-9]+)(?:\.([0-9]+)(?:\.([0-9]+)(?:\.([0-9]+))?)?)?(?:-([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*))?(?:\+([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*))?\z")]
    private static partial Regex ParseRegex();
}