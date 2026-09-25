using System.Collections;
using Everywhere.Collections;

namespace Everywhere.Abstractions.Tests.Collections;

public class ValueArrayTests
{
    [Test]
    public void Equals_DefaultNullAndEmpty_HaveEqualValuesAndHashes()
    {
        var values = new[] { default(ValueArray<string>), new ValueArray<string>(null), new ValueArray<string>([]) };
        foreach (var left in values)
        foreach (var right in values)
        {
            Assert.That(left == right, Is.True);
            Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
            Assert.That(((IStructuralEquatable)left).Equals(right, StringComparer.Ordinal), Is.True);
            Assert.That(left.GetHashCode(StringComparer.Ordinal), Is.EqualTo(right.GetHashCode(StringComparer.Ordinal)));
        }
        Assert.That(values[0] == new ValueArray<string>(["low"]), Is.False);
    }

    [Test]
    public void RecordEquality_SeparateArrays_UsesOrderedElementValues()
    {
        var left = new ValueArrayTestRecord { Values = new ValueArray<string>(["low", "high"]) };
        var right = new ValueArrayTestRecord { Values = new ValueArray<string>(["low", "high"]) };
        Assert.That(left == right, Is.True);
        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
        Assert.That(left == right with { Values = new ValueArray<string>(["high", "low"]) }, Is.False);
    }
}

internal sealed record ValueArrayTestRecord
{
    public ValueArray<string> Values { get; init; }
}
