using System.Text;

namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Represents the opaque integer sequence that identifies one active UI Automation element incarnation.
/// </summary>
/// <remarks>
/// RuntimeId components are compared as opaque values and are never interpreted as window, process, provider, or generation identifiers.
/// </remarks>
public sealed class UIAutomationRuntimeId : IEquatable<UIAutomationRuntimeId>
{
    /// <summary>
    /// Gets the number of opaque RuntimeId components.
    /// </summary>
    public int Length => _values.Length;

    /// <summary>
    /// Gets the immutable RuntimeId components.
    /// </summary>
    public ReadOnlySpan<int> Values => _values;

    private readonly int[] _values;
    private readonly int _hashCode;

    internal UIAutomationRuntimeId(ReadOnlySpan<int> values)
    {
        _values = [.. values];
        _hashCode = CalculateHashCode(values);
    }

    /// <inheritdoc />
    public bool Equals(UIAutomationRuntimeId? other) => ReferenceEquals(this, other) ||
        other is not null && _hashCode == other._hashCode && Values.SequenceEqual(other.Values);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UIAutomationRuntimeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _hashCode;

    internal static int CalculateHashCode(ReadOnlySpan<int> values)
    {
        var hashCode = new HashCode();
        foreach (var value in values) hashCode.Add(value);
        return hashCode.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder(_values.Length * 9 + 4).Append("uia:");
        for (var index = 0; index < _values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append('.');
            }

            builder.Append(_values[index].ToString("X"));
        }

        return builder.ToString();
    }
}

/// <summary>
/// Compares durable UI Automation RuntimeIds and supports allocation-free lookups from operation-scoped RuntimeId spans.
/// </summary>
public sealed class UIAutomationRuntimeIdComparer : IEqualityComparer<UIAutomationRuntimeId>,
    IAlternateEqualityComparer<ReadOnlySpan<int>, UIAutomationRuntimeId>
{
    /// <summary>
    /// Gets the shared stateless comparer.
    /// </summary>
    public static UIAutomationRuntimeIdComparer Instance { get; } = new();

    private UIAutomationRuntimeIdComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(UIAutomationRuntimeId? x, UIAutomationRuntimeId? y) => x?.Equals(y) ?? y is null;

    /// <inheritdoc />
    public int GetHashCode(UIAutomationRuntimeId obj) => obj.GetHashCode();

    /// <inheritdoc />
    public UIAutomationRuntimeId Create(ReadOnlySpan<int> alternate) => new(alternate);

    /// <inheritdoc />
    public bool Equals(ReadOnlySpan<int> alternate, UIAutomationRuntimeId other) => alternate.SequenceEqual(other.Values);

    /// <inheritdoc />
    public int GetHashCode(ReadOnlySpan<int> alternate) => UIAutomationRuntimeId.CalculateHashCode(alternate);
}