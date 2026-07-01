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

    [TestCase("0.8.0")]
    [TestCase("0.8.0-canary.20260530.2")]
    public void ToString_RoundTripsParsedVersions(string input)
    {
        Assert.That(SemanticVersion.TryParse(input, out var version), Is.True);

        Assert.That(version!.ToString(), Is.EqualTo(input));
    }

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("0.8.x")]
    [TestCase("0.8.0-")]
    [TestCase("0.8.0-canary.")]
    [TestCase("0.8.0-canary..2")]
    [TestCase("-1.8.0")]
    public void TryParse_InvalidVersion_ReturnsFalse(string input)
    {
        Assert.That(SemanticVersion.TryParse(input, out _), Is.False);
    }

    [Test]
    public void CompareTo_StableVersion_IsNewerThanSameBaseCanaryVersion()
    {
        Assert.That(
            SemanticVersion.Parse("0.8.0")!,
            Is.GreaterThan(SemanticVersion.Parse("0.8.0-canary.20260530.2")!));
    }

    [Test]
    public void CompareTo_CanaryVersions_CompareDateAndNumberPartsNumerically()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SemanticVersion.Parse("0.8.0-canary.20260530.2")!,
                Is.GreaterThan(SemanticVersion.Parse("0.8.0-canary.20260529.99")!));
            Assert.That(
                SemanticVersion.Parse("0.8.0-canary.20260530.10")!,
                Is.GreaterThan(SemanticVersion.Parse("0.8.0-canary.20260530.2")!));
        });
    }
}
