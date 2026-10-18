using Everywhere.Common;

namespace Everywhere.Abstractions.Tests.Common;

/// <summary>
/// Exercises public compatibility declarations, including prerelease boundaries and exact identity.
/// </summary>
public sealed class SemanticVersionRangeTests
{
    [TestCase("[1.2,2.0)", "1.2.0", true)]
    [TestCase("(1.2,2.0]", "1.2.0", false)]
    [TestCase("(1.2,2.0]", "2.0.0", true)]
    [TestCase("[1.2,2.0)", "2.0.0", false)]
    [TestCase("[1.2,2.0)", "1.2.0-preview.1", false)]
    [TestCase("[1.2,2.0)", "2.0.0-preview.1", true)]
    [TestCase("[1.2,2.0-0)", "2.0.0-preview.1", false)]
    [TestCase("[1.2-preview.2,2.0)", "1.2.0-preview.10", true)]
    [TestCase("[1.2-preview.2]", "1.2.0-PREVIEW.02+commit", true)]
    [TestCase("[1.2]", "1.2.1", false)]
    [TestCase("1.2", "1.3.0", true)]
    [TestCase("(,1.2]", "0.9.0", true)]
    [TestCase("(1.2,)", "1.2.0", false)]
    [TestCase("(,)", "0.0.0-preview.1", true)]
    public void Contains_DeclaredInterval_AppliesBounds(string declaration, string candidate, bool isExpected)
    {
        var version = SemanticVersion.Parse(candidate) ?? throw new AssertionException(candidate);
        Assert.That(SemanticVersionRange.Parse(declaration).Contains(version), Is.EqualTo(isExpected));
    }

    [TestCase("[ 01.2.0.0-PREVIEW.02+build ]", "[1.2.0-preview.2]")]
    [TestCase("1.2", "[1.2.0,)")]
    [TestCase("[1.2, 2.0)", "[1.2.0,2.0.0)")]
    [TestCase("[1.2,1.2.0]", "[1.2.0]")]
    [TestCase("(,)", "(,)")]
    public void ToString_ValidInterval_NormalizesBounds(string declaration, string expected)
    {
        var range = SemanticVersionRange.Parse(declaration);
        Assert.That(range.ToString(), Is.EqualTo(expected));
        Assert.That(SemanticVersionRange.Parse(range.ToString()).ToString(), Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("1.*")]
    [TestCase("[2,1]")]
    [TestCase("(1,1]")]
    [TestCase("(1)")]
    [TestCase("[]")]
    [TestCase("[,1]")]
    [TestCase("[1,]")]
    [TestCase("[1,2,3]")]
    [TestCase("[1,2")]
    public void TryParse_InvalidDeclaration_RejectsInput(string declaration)
    {
        Assert.That(SemanticVersionRange.TryParse(declaration, out _), Is.False);
    }
}