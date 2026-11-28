using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public sealed class ChatWorkspaceLifecycleTests
{
    [Test]
    public async Task RootChatContext_Constructor_DoesNotCreateWorkingDirectoryUntilAccessed()
    {
        var data = new ChatData();
        await using var context = new RootChatContext(data, null);

        var expectedPath = RootChatContext.GetWorkingDirectoryPath(data.Metadata);
        Assert.That(Directory.Exists(expectedPath), Is.False);
    }

    [Test]
    public async Task RootChatContext_WorkingDirectory_CreatesDirectoryUnderUserWorkspaces()
    {
        var data = new ChatData();
        await using var context = new RootChatContext(data, null);

        var workingDirectory = context.WorkingDirectory;
        var expectedPrefix = RuntimeConstants.GetUserFolderPath("workspaces");

        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(workingDirectory, Is.EqualTo(RootChatContext.GetWorkingDirectoryPath(data.Metadata)));
                Assert.That(PathUtilities.IsResolvedPathInsideDirectory(workingDirectory, expectedPrefix), Is.True);
                Assert.That(Directory.Exists(workingDirectory), Is.True);
            }
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [Test]
    public async Task RootChatContext_WorkingDirectory_RecreatesDirectoryIfRemovedExternally()
    {
        var data = new ChatData();
        await using var context = new RootChatContext(data, null);

        var workingDirectory = context.WorkingDirectory;
        try
        {
            Assert.That(Directory.Exists(workingDirectory), Is.True);
            Directory.Delete(workingDirectory);
            Assert.That(Directory.Exists(workingDirectory), Is.False);

            var ensuredAgain = context.WorkingDirectory;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ensuredAgain, Is.EqualTo(workingDirectory));
                Assert.That(Directory.Exists(ensuredAgain), Is.True);
            }
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [AvaloniaTest]
    public async Task DeletePermanentlyAsync_WhenWorkspaceIsEmpty_RemovesWorkspaceAndEmptyDateDirectory()
    {
        var storage = Substitute.For<IChatContextStorage>();
        await using var manager = new ChatContextManager(
            new Settings(new ServiceCollection().BuildServiceProvider()),
            storage,
            NullLogger<ChatContextManager>.Instance);

        // Use a dedicated UTC date so concurrent or sequential UtcNow tests cannot leave siblings in this bucket.
        var isolatedDate = new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var data = new ChatData(new ChatMetadata(Guid.CreateVersion7(), isolatedDate, isolatedDate, null));
        await using var context = new RootChatContext(data, null);
        var workspaceDir = context.WorkingDirectory;
        var dateDir = Path.GetDirectoryName(workspaceDir);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dateDir, Is.Not.Null);
            Assert.That(Directory.Exists(workspaceDir), Is.True);
        }

        await manager.DeletePermanentlyAsync(data.Metadata);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.Exists(workspaceDir), Is.False);
            Assert.That(Directory.Exists(dateDir), Is.False);
        }
    }

    [AvaloniaTest]
    public async Task DeletePermanentlyAsync_WhenWorkspaceHasFiles_PreservesWorkspaceDirectory()
    {
        var storage = Substitute.For<IChatContextStorage>();
        await using var manager = new ChatContextManager(
            new Settings(new ServiceCollection().BuildServiceProvider()),
            storage,
            NullLogger<ChatContextManager>.Instance);

        var data = new ChatData();
        await using var context = new RootChatContext(data, null);
        var workspaceDir = context.WorkingDirectory;
        var artifactPath = Path.Combine(workspaceDir, "artifact.txt");
        await File.WriteAllTextAsync(artifactPath, "keep me");

        try
        {
            await manager.DeletePermanentlyAsync(data.Metadata);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(workspaceDir), Is.True);
                Assert.That(File.Exists(artifactPath), Is.True);
            }
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }
}
