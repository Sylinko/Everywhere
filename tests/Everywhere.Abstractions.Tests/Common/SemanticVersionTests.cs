using Everywhere.Common;

namespace Everywhere.Abstractions.Tests.Common;

[TestFixture]
public class SemanticVersionTests
{
    [Test]
    public void TryParse_StableVersion_ParsesStableChannel()
    {
        var success = SemanticVersion.TryParse("0.8.0", out var version);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(version, Is.Not.Null);
            Assert.That(version!.Major, Is.EqualTo(0));
            Assert.That(version.Minor, Is.EqualTo(8));
            Assert.That(version.Build, Is.EqualTo(0));
            Assert.That(version.Revision, Is.EqualTo(0));
            Assert.That(version.Suffix, Is.Null);
            Assert.That(version.Channel, Is.EqualTo(UpdateChannel.Stable));
        });
    }

    [Test]
    public void TryParse_CanaryVersion_ParsesSuffixAndChannel()
    {
        var success = SemanticVersion.TryParse("0.8.0-canary.20260530.2", out var version);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True);
            Assert.That(version, Is.Not.Null);
            Assert.That(version!.Major, Is.EqualTo(0));
            Assert.That(version.Minor, Is.EqualTo(8));
            Assert.That(version.Build, Is.EqualTo(0));
            Assert.That(version.Suffix, Is.EqualTo("canary.20260530.2"));
            Assert.That(version.Channel, Is.EqualTo(UpdateChannel.Canary));
        });
    }

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("0.8.x")]
    [TestCase("0.8.0-")]
    [TestCase("0.8.0-canary.")]
    [TestCase("0.8.0-canary..2")]
    [TestCase("-1.8.0")]
    [TestCase("1.0.0+")]
    [TestCase("1.0.0+build..2")]
    [TestCase("1.0.0-preview_2")]
    [TestCase("1.0.0\n")]
    [TestCase("2147483648.0.0")]
    public void TryParse_InvalidVersion_ReturnsFalse(string input)
    {
        Assert.That(SemanticVersion.TryParse(input, out _), Is.False);
    }

    /// <summary>
    /// Keeps the pre-change ordering of all 88 local release tags and 10 migration declarations
    /// captured on 2026-10-17 (91 distinct versions), without requiring Git during test execution.
    /// </summary>
    [Test]
    public void CompareTo_HistoricalTagsAndMigrations_PreservesRecordedOrder()
    {
        var expected = File.ReadAllLines(Path.Combine(TestContext.CurrentContext.TestDirectory, "Common", "HistoricalVersions.txt"));
        var versions = expected.Select(Parse).ToArray();

        Assert.That(versions.Reverse().Order().Select(version => version.ToString()), Is.EqualTo(expected));
        Assert.That(versions.Distinct().Count(), Is.EqualTo(expected.Length));
    }

    [TestCase("1.0.0-beta.2", "1.0.0-beta.10")]
    [TestCase("1.0.0-canary.1", "1.0.0-preview.1")]
    [TestCase("1.0.0-1", "1.0.0-alpha")]
    [TestCase("1.0.0-preview", "1.0.0-preview.1")]
    [TestCase("1.0.0-preview.99999999999999999999", "1.0.0-preview.100000000000000000000")]
    [TestCase("1.0.0-preview.10", "1.0.0")]
    public void CompareTo_ArbitraryPrereleaseLabels_UsesSegmentOrder(string earlier, string later)
    {
        Assert.That(Parse(earlier).CompareTo(Parse(later)), Is.LessThan(0));
    }

    [Test]
    public void Equals_EquivalentVersions_AgreesWithOrderingAndHashing()
    {
        var first = Parse("01.02.0.0-PREVIEW.002+first");
        var second = Parse("1.2-preview.2+second");

        Assert.Multiple(() =>
        {
            Assert.That(first.CompareTo(second), Is.Zero);
            Assert.That(first == second, Is.True);
            Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
            Assert.That(first.ToNormalizedString(), Is.EqualTo("1.2.0-preview.2"));
            Assert.That(first.ToString(), Is.EqualTo("1.2.0-PREVIEW.002+first"));
            Assert.That(new HashSet<SemanticVersion> { first, second }, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Channel_UnknownOrNumericLabel_DoesNotSelectKnownFeed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Parse("1.0-preview.2").Channel, Is.EqualTo(UpdateChannel.Unknown));
            Assert.That(Parse("1.0-1").Channel, Is.EqualTo(UpdateChannel.Unknown));
            Assert.That(Parse("1.0-stable").Channel, Is.EqualTo(UpdateChannel.Unknown));
            Assert.That(Parse("1.0+commit").Channel, Is.EqualTo(UpdateChannel.Stable));
        });
    }

    [TestCase("1.2.3.4", "1.2.3.4")]
    [TestCase("1", "1.0.0")]
    [TestCase("1.02", "1.2.0")]
    public void ToNormalizedString_NumericComponents_PreservesRevisionAndFillsDefaults(string input, string expected)
    {
        Assert.That(Parse(input).ToNormalizedString(), Is.EqualTo(expected));
    }

    /// <summary>
    /// Fails clearly if a test's release version is malformed.
    /// </summary>
    private static SemanticVersion Parse(string value)
    {
        return SemanticVersion.Parse(value) ?? throw new AssertionException($"Invalid fixture version: {value}");
    }
}
