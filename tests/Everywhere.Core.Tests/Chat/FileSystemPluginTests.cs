using System.Reflection;
using Everywhere.Chat;
using Everywhere.Chat.Documents;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public class FileSystemPluginTests
{
    [Test]
    public void BuildSearchOutput_GroupsOccurrencesOnTheSameLineAndKeepsLocations()
    {
        var sink = new ChatPluginDisplaySink();
        var path = Path.Combine(Path.GetTempPath(), "same-line.txt");
        List<FileContentMatch> matches =
        [
            new(path, "needle and needle", 5, 1, 6),
            new(path, "needle and needle", 5, 12, 6),
            new(path, "another needle", 9, 9, 6)
        ];
        var method = typeof(FileSystemPlugin).GetMethod(
            "BuildSearchOutput",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);
        var node = (PromptNode)method!.Invoke(null, [sink, matches, "needle", 20, false])!;
        var output = node.ToString();
        var reference = sink
            .OfType<ChatPluginFileReferencesDisplayBlock>()
            .SelectMany(static block => block.References)
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("Found 3 occurrences on 2 matching lines in 1 file"));
            Assert.That(output.Split(Environment.NewLine).Count(static line => line.StartsWith("5:")), Is.EqualTo(1));
            Assert.That(reference.Locations, Has.Count.EqualTo(3));
        });
    }

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
            Assert.That(PathContainment.IsInsideDirectory(root, root), Is.True);
            Assert.That(PathContainment.IsInsideDirectory(child, root), Is.True);
        });
    }

    [Test]
    public void IsPathInsideDirectory_RejectsSiblingPrefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "work");
        var siblingPrefix = Path.Combine(parent, "work2", "file.txt");

        Assert.That(PathContainment.IsInsideDirectory(siblingPrefix, root), Is.False);
    }

    [Test]
    public void IsPathInsideDirectory_RejectsLinkThatResolvesOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Ignore($"Symbolic links are unavailable: {ex.Message}");
            }

            Assert.That(
                PathContainment.IsInsideDirectory(Path.Combine(link, "file.txt"), root),
                Is.False);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Test]
    public void GetCommonParentDirectory_ReturnsParentOnlyWhenAllPathsShareIt()
    {
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var commonPaths = new[] { Path.Combine(parent, "one.txt"), Path.Combine(parent, "two.txt") };
        var differentPaths = new[] { Path.Combine(parent, "one.txt"), Path.Combine(parent, "nested", "two.txt") };
        var commonParent = PathContainment.GetCommonParentDirectory(commonPaths);
        var differentParent = PathContainment.GetCommonParentDirectory(differentPaths);

        Assert.Multiple(() =>
        {
            Assert.That(commonParent, Is.EqualTo(parent));
            Assert.That(differentParent, Is.Null);
        });
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
            var plugin = new FileSystemPlugin(
                new Settings(Substitute.For<IServiceProvider>()),
                new FileHandlerContextFactory([new PdfFileHandler(), new TextFileHandler(), new BinaryFileHandler()]),
                Substitute.For<ILogger<FileSystemPlugin>>());

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

    [Test]
    public void WriteToFileAsync_DeniedConsent_DoesNotCreateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var displaySink = new ChatPluginDisplaySink();
        var userInterface = Substitute.For<IChatPluginUserInterface>();
        userInterface.DisplaySink.Returns(displaySink);
        userInterface.RequestConsentAsync(
                Arg.Any<string?>(),
                Arg.Any<IDynamicLocaleKey>(),
                Arg.Any<ChatPluginDisplayBlock?>(),
                Arg.Any<RequestConsentRememberMasks>(),
                Arg.Any<IReadOnlyList<RequestConsentCustomOption>?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RequestConsentResult(false, "denied")));
        var plugin = new FileSystemPlugin(
            new Settings(Substitute.For<IServiceProvider>()),
            new FileHandlerContextFactory([new PdfFileHandler(), new TextFileHandler(), new BinaryFileHandler()]),
            Substitute.For<ILogger<FileSystemPlugin>>());

        var method = typeof(FileSystemPlugin).GetMethod(
            "WriteToFileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        var task = (Task)method!.Invoke(
            plugin,
            [userInterface, new ChatContext(), path, "content", false, CancellationToken.None])!;

        Assert.ThrowsAsync<HandledException>(async () => await task);
        Assert.That(File.Exists(path), Is.False);
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
