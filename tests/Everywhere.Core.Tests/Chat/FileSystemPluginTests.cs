using Everywhere.Chat;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins;
using Everywhere.Core.I18N;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Reflection;

namespace Everywhere.Core.Tests.Chat;

public class FileSystemPluginTests
{
    [Test]
    public void ExpandFullPath_ResolvesRelativePathAgainstWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var result = FileSystemPlugin.ExpandFullPath(workingDirectory, Path.Combine("nested", "file.txt"));

        Assert.That(result, Is.EqualTo(Path.GetFullPath(Path.Combine(workingDirectory, "nested", "file.txt"))));
    }

    [Test]
    public void ExpandFullPath_DoesNotChangeCurrentDirectory()
    {
        var currentDirectory = Environment.CurrentDirectory;
        var workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        _ = FileSystemPlugin.ExpandFullPath(workingDirectory, "file.txt");

        Assert.That(Environment.CurrentDirectory, Is.EqualTo(currentDirectory));
    }

    [Test]
    public void ExpandFullPath_ExpandsEnvironmentVariablesBeforeResolving()
    {
        var variableName = "EVERYWHERE_TEST_PATH_" + Guid.NewGuid().ToString("N");
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        try
        {
            var workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(variableName, "expanded");

            var result = FileSystemPlugin.ExpandFullPath(
                workingDirectory,
                Path.Combine($"%{variableName}%", "file.txt"));

            Assert.That(result, Is.EqualTo(Path.GetFullPath(Path.Combine(workingDirectory, "expanded", "file.txt"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Test]
    public void IsPathInsideDirectory_AcceptsDirectoryAndDescendants()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "work");
        var child = Path.Combine(root, "nested", "file.txt");

        Assert.Multiple(() =>
        {
            Assert.That(FileSystemPlugin.IsPathInsideDirectory(root, root), Is.True);
            Assert.That(FileSystemPlugin.IsPathInsideDirectory(child, root), Is.True);
        });
    }

    [Test]
    public void IsPathInsideDirectory_RejectsSiblingPrefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "work");
        var siblingPrefix = Path.Combine(parent, "work2", "file.txt");

        Assert.That(FileSystemPlugin.IsPathInsideDirectory(siblingPrefix, root), Is.False);
    }

    [Test]
    public void GetWriteConsentDescriptionKey_UsesCreateDescriptionWhenFileDoesNotExist()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                FileSystemPlugin.GetWriteConsentDescriptionKey(append: false, fileExists: false),
                Is.EqualTo(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_CreateConsent_Description));
            Assert.That(
                FileSystemPlugin.GetWriteConsentDescriptionKey(append: true, fileExists: false),
                Is.EqualTo(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_CreateConsent_Description));
        });
    }

    [Test]
    public void GetWriteConsentDescriptionKey_UsesAppendOrOverwriteDescriptionForExistingFile()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                FileSystemPlugin.GetWriteConsentDescriptionKey(append: true, fileExists: true),
                Is.EqualTo(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_AppendConsent_Description));
            Assert.That(
                FileSystemPlugin.GetWriteConsentDescriptionKey(append: false, fileExists: true),
                Is.EqualTo(LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_OverwriteConsent_Description));
        });
    }

    [Test]
    public async Task ReplaceFileContentAsync_NoActualReplacement_ReturnsNoOpWithoutAppendingDifference()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        try
        {
            var displaySink = new ChatPluginDisplaySink();
            var chatContext = new ChatContext();
            var plugin = new FileSystemPlugin(Substitute.For<ILogger<FileSystemPlugin>>());

            var result = await InvokeReplaceFileContentAsync(
                plugin,
                displaySink,
                chatContext,
                filePath,
                ["missing"],
                ["replacement"],
                isRegex: false,
                ignoreCase: false);
            var fileContent = await File.ReadAllTextAsync(filePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo("No content was replaced."));
                Assert.That(fileContent, Is.EqualTo("hello world"));
                Assert.That(displaySink.OfType<ChatPluginFileDifferenceDisplayBlock>(), Is.Empty);
            });
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    private static Task<string> InvokeReplaceFileContentAsync(
        FileSystemPlugin plugin,
        IChatPluginDisplaySink displaySink,
        ChatContext chatContext,
        string path,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> replacements,
        bool isRegex,
        bool ignoreCase)
    {
        var method = typeof(FileSystemPlugin).GetMethod(
            "ReplaceFileContentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);

        // The plugin now receives the complete invocation UI facade so it can publish a transient
        // ActivityPreview as well as durable display blocks. This test only exercises the latter;
        // keep the sink supplied by the test and let NSubstitute provide the unused UI members.
        var userInterface = Substitute.For<IChatPluginUserInterface>();
        userInterface.DisplaySink.Returns(displaySink);

        return (Task<string>)method!.Invoke(
            plugin,
            [
                userInterface,
                chatContext,
                path,
                patterns,
                replacements,
                isRegex,
                ignoreCase,
                CancellationToken.None
            ])!;
    }
}
