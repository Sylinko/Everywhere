using Everywhere.Common;

namespace Everywhere.Abstractions.Tests.Common;

[TestFixture]
public sealed class RuntimeConstantsTests
{
    [Test]
    public void EnsureSessionFolderPath_ValidCategoryAndSessionId_CreatesExpectedDirectoryUnderUserFolder()
    {
        var path = RuntimeConstants.EnsureSessionFolderPath("mod-host", "session-123", "logs");
        var expected = Path.GetFullPath(Path.Combine(RuntimeConstants.UserFolderPath, "sessions", "mod-host", "session-123", "logs"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(path, Is.EqualTo(expected));
            Assert.That(Directory.Exists(path), Is.True);
        }
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(".")]
    [TestCase("..")]
    [TestCase("a/b")]
    [TestCase(@"a\b")]
    public void EnsureSessionFolderPath_InvalidSegment_ThrowsArgumentException(string invalidSegment)
    {
        Assert.That(
            () => RuntimeConstants.EnsureSessionFolderPath("mod-host", invalidSegment),
            Throws.InstanceOf<ArgumentException>());
        Assert.That(
            () => RuntimeConstants.EnsureSessionFolderPath(invalidSegment, "session-123"),
            Throws.InstanceOf<ArgumentException>());
    }
}
