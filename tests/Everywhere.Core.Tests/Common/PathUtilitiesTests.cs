using Everywhere.Common;

namespace Everywhere.Core.Tests.Common;

public sealed class PathUtilitiesTests
{
    [Test]
    public void ResolvePath_LinkTargetTraversalAcrossAnotherLink_DoesNotCollapseBeforeLinkResolution()
    {
        var root = CreateTemporaryDirectory();
        var outside = Path.Combine(root, "outside");
        var nested = Path.Combine(outside, "nested");
        var resultPath = Path.Combine(outside, "result.txt");
        Directory.CreateDirectory(nested);
        File.WriteAllText(resultPath, "content");

        try
        {
            var redirect = Path.Combine(root, "redirect");
            var link = Path.Combine(root, "link");
            CreateDirectoryLinkOrIgnore(redirect, nested);
            CreateFileLinkOrIgnore(link, Path.Combine("redirect", "..", "result.txt"));

            var result = PathUtilities.ResolvePath(link, PathResolutionMode.FollowFinalComponent);
            var customTransitions = GetTransitionsInsideRoot(result, root);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True, result.Failure?.Message);
                Assert.That(result.ResolvedPath, Is.EqualTo(ResolveFinalPath(resultPath)));
                Assert.That(customTransitions, Has.Length.EqualTo(2));
                Assert.That(customTransitions[0].LinkTarget, Is.EqualTo(Path.Combine("redirect", "..", "result.txt")));
                Assert.That(customTransitions[1].Path, Is.EqualTo(ResolveFinalPath(redirect, PathResolutionMode.PreserveFinalComponent)));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void ResolvePath_NonDirectoryIntermediateComponent_ReturnsDetailedFailure()
    {
        var root = CreateTemporaryDirectory();
        var filePath = Path.Combine(root, "file.txt");
        File.WriteAllText(filePath, "content");

        try
        {
            var result = PathUtilities.ResolvePath(
                Path.Combine(filePath, "child.txt"),
                PathResolutionMode.FollowFinalComponent);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.Failure?.Kind, Is.EqualTo(PathResolutionFailureKind.NotDirectory));
                Assert.That(result.Failure?.Path, Is.EqualTo(ResolveFinalPath(filePath)));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void IsResolvedPathInsideDirectory_FileSystemRoot_ContainsDescendant()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())) ??
            throw new AssertionException("The temporary path does not have a file-system root.");

        Assert.That(PathUtilities.IsResolvedPathInsideDirectory(Path.GetTempPath(), root), Is.True);
    }

    [Test]
    public void ResolvePath_FinalLink_UsesRequestedFinalComponentMode()
    {
        var root = CreateTemporaryDirectory();
        var targetPath = Path.Combine(root, "target.txt");
        var linkPath = Path.Combine(root, "link.txt");
        File.WriteAllText(targetPath, "content");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "target.txt");

            var followed = PathUtilities.ResolvePath(linkPath, PathResolutionMode.FollowFinalComponent);
            var preserved = PathUtilities.ResolvePath(linkPath, PathResolutionMode.PreserveFinalComponent);
            var followedCustomTransitions = GetTransitionsInsideRoot(followed, root);
            var preservedCustomTransitions = GetTransitionsInsideRoot(preserved, root);

            Assert.Multiple(() =>
            {
                Assert.That(followed.ResolvedPath, Is.EqualTo(ResolveFinalPath(targetPath)));
                Assert.That(followedCustomTransitions, Has.Length.EqualTo(1));
                Assert.That(preserved.ResolvedPath, Is.EqualTo(ResolveFinalPath(linkPath, PathResolutionMode.PreserveFinalComponent)));
                Assert.That(preservedCustomTransitions, Is.Empty);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void ResolvePath_MissingFinalEntryThroughParentLink_ResolvesParentAndRetainsFilename()
    {
        var root = CreateTemporaryDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var linkDirectory = Path.Combine(root, "link");
        Directory.CreateDirectory(targetDirectory);

        try
        {
            CreateDirectoryLinkOrIgnore(linkDirectory, "target");
            var result = PathUtilities.ResolvePath(
                Path.Combine(linkDirectory, "new.txt"),
                PathResolutionMode.PreserveFinalComponent);
            var customTransitions = GetTransitionsInsideRoot(result, root);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True, result.Failure?.Message);
                Assert.That(result.ResolvedPath, Is.EqualTo(ResolveFinalPath(Path.Combine(targetDirectory, "new.txt"))));
                Assert.That(customTransitions, Has.Length.EqualTo(1));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void ResolvePath_MissingTail_PreservesPathForOperationValidation()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            var result = PathUtilities.ResolvePath(
                Path.Combine(root, "missing", "nested", "file.txt"),
                PathResolutionMode.PreserveFinalComponent);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.True, result.Failure?.Message);
                Assert.That(
                    result.ResolvedPath,
                    Is.EqualTo(Path.Combine(ResolveFinalPath(root), "missing", "nested", "file.txt")));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestCase("file:///tmp/file.txt", true)]
    [TestCase("https://example.com/file.txt", true)]
    [TestCase("/tmp/file.txt", false)]
    [TestCase("relative/path.txt", false)]
    public void HasExplicitUriScheme_PathSyntax_ReturnsExpectedResult(string path, bool expected)
    {
        Assert.That(PathUtilities.HasExplicitUriScheme(path), Is.EqualTo(expected));
    }

    [Test]
    public void ResolvePath_TraversalExceedsBound_ReturnsDetailedFailure()
    {
        var root = CreateTemporaryDirectory();
        var targetDirectory = Path.Combine(root, "target");
        Directory.CreateDirectory(targetDirectory);

        try
        {
            const int linkCount = 41;
            for (var index = linkCount - 1; index >= 0; index--)
            {
                var target = index == linkCount - 1 ? "target" : $"link-{index + 1}";
                CreateDirectoryLinkOrIgnore(Path.Combine(root, $"link-{index}"), target);
            }

            var result = PathUtilities.ResolvePath(
                Path.Combine(root, "link-0", "file.txt"),
                PathResolutionMode.FollowFinalComponent);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.Failure?.Kind, Is.EqualTo(PathResolutionFailureKind.TooManyLinks));
                Assert.That(result.Failure?.Path, Does.Contain("link-"));
                Assert.That(result.LinkTransitions, Has.Count.EqualTo(40));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "everywhere-path-containment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateDirectoryLinkOrIgnore(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Ignore($"Symbolic links are unavailable in this test environment: {ex.Message}");
        }
    }

    private static void CreateFileLinkOrIgnore(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Ignore($"Symbolic links are unavailable in this test environment: {ex.Message}");
        }
    }

    private static string ResolveFinalPath(
        string path,
        PathResolutionMode mode = PathResolutionMode.FollowFinalComponent)
    {
        var result = PathUtilities.ResolvePath(path, mode);
        return result.ResolvedPath ?? throw new AssertionException($"Could not resolve test path '{path}': {result.Failure?.Message}");
    }

    private static PathLinkTransition[] GetTransitionsInsideRoot(PathResolutionResult result, string root)
    {
        var resolvedRoot = ResolveFinalPath(root);
        return result.LinkTransitions
            .Where(transition => PathUtilities.IsResolvedPathInsideDirectory(transition.Path, resolvedRoot))
            .ToArray();
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
