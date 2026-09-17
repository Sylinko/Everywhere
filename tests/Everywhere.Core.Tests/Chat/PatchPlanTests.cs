using System.Text;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;
using Everywhere.Common;

namespace Everywhere.Core.Tests.Chat;

public class PatchPlanTests
{
    [Test]
    public async Task BuildAsync_Update_PreservesEncodingAndLineEndings()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllBytesAsync(
            path,
            encoding.GetPreamble().Concat(encoding.GetBytes("before\r\nold\r\nafter\r\n")).ToArray());

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                 before
                -old
                +new
                 after
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();
            Assert.That(file, Is.TypeOf<PatchUpdatePlanFile>());
            var update = (PatchUpdatePlanFile)file;

            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("before\r\nnew\r\nafter\r\n"));
                Assert.That(file.MatchDiagnostics, Is.Empty);
                Assert.That(update.ProposedBytes[..encoding.GetPreamble().Length], Is.EqualTo(encoding.GetPreamble()));
                Assert.That(file.Original.Preamble, Is.EqualTo(encoding.GetPreamble()));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_RepeatedHunkContext_UpdatesFirstMatch()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "same\nold\nsame\nold\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@ same
                -old
                +new
                *** End Patch
                """,
                root);

            var file = plan.Files.Single();
            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("same\nnew\nsame\nold\n"));
                Assert.That(file.MatchDiagnostics, Is.Empty);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_UnicodeCompatibilityFallback_AppliesReplacement()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "import asyncio  # local import – avoids top‑level dep\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -import asyncio  # local import - avoids top-level dep
                +import asyncio  # replacement
                *** End Patch
                """,
                root);

            var file = plan.Files.Single();
            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("import asyncio  # replacement\n"));
                Assert.That(file.MatchDiagnostics, Has.Count.EqualTo(1));
                Assert.That(file.MatchDiagnostics[0].HunkNumber, Is.EqualTo(1));
                Assert.That(file.MatchDiagnostics[0].HeaderLineNumber, Is.EqualTo(3));
                Assert.That(file.MatchDiagnostics[0].Kind, Is.EqualTo(PatchMatchKind.UnicodeCompatibilityFallback));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_OuterWhitespaceFallback_WritesAdditionExactly()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "    old\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -old
                +  new
                *** End Patch
                """,
                root);

            var file = plan.Files.Single();
            Assert.Multiple(() =>
            {
                Assert.That(file.ProposedContent, Is.EqualTo("  new\n"));
                Assert.That(file.MatchDiagnostics, Has.Count.EqualTo(1));
                Assert.That(file.MatchDiagnostics[0].Kind, Is.EqualTo(PatchMatchKind.OuterWhitespaceFallback));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_TrailingWhitespaceFallback_RecordsDiagnostic()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "old  \n");

        try
        {
            var plan = await BuildAsync(
                "*** Begin Patch\n*** Update File: file.txt\n@@\n-old\t\n+new\n*** End Patch",
                root);

            Assert.That(
                plan.Files.Single().MatchDiagnostics.Single().Kind,
                Is.EqualTo(PatchMatchKind.TrailingWhitespaceFallback));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_AnchoredWhitespaceFallback_RecordsContextDiagnostic()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "    section\nold\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@ section
                -old
                +new
                *** End Patch
                """,
                root);

            Assert.That(
                plan.Files.Single().MatchDiagnostics.Single().Kind,
                Is.EqualTo(PatchMatchKind.ContextOuterWhitespaceFallback));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_OverlappingHunks_FailsBeforeCreatingOutput()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "a\nb\nc\n");

        try
        {
            var exception = Assert.ThrowsAsync<PatchMatchException>(async () =>
                await BuildAsync(
                    """
                    *** Begin Patch
                    *** Update File: file.txt
                    @@
                     a
                    -b
                    +B
                    @@
                     b
                    -c
                    +C
                    *** End Patch
                    """,
                    root));

            Assert.That(exception!.Message, Does.Contain("overlap"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_OutOfOrderHunks_FailsClosed()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "first\nsecond\nthird\n");

        try
        {
            var exception = Assert.ThrowsAsync<PatchMatchException>(async () =>
                await BuildAsync(
                    """
                    *** Begin Patch
                    *** Update File: file.txt
                    @@
                    -third
                    +THIRD
                    @@
                    -second
                    +SECOND
                    *** End Patch
                    """,
                    root));

            Assert.That(exception!.Message, Does.Contain("out of order"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_BareHunks_LocateNextMatchInPatchOrder()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "section A\nold\nsection B\nold\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -old
                +new A

                @@
                -old
                +new B
                *** End Patch
                """,
                root);

            Assert.That(plan.Files.Single().ProposedContent, Is.EqualTo("section A\nnew A\nsection B\nnew B\n"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_ContextInsertion_InsertsImmediatelyAfterAnchor()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "method A\nmethod B\nafter\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@ method B
                +inserted
                *** End Patch
                """,
                root);

            Assert.That(plan.Files.Single().ProposedContent, Is.EqualTo("method A\nmethod B\ninserted\nafter\n"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void BuildAsync_MissingHunkTarget_ReportsFileHunkAndPatchLine()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "actual\n");

        try
        {
            var exception = Assert.ThrowsAsync<PatchMatchException>(async () =>
                await BuildAsync(
                    """
                    *** Begin Patch
                    *** Update File: file.txt
                    @@
                    -missing
                    +new
                    *** End Patch
                    """,
                    root));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain($"Patch target '{ResolveFinalPath(path)}'"));
                Assert.That(exception.Message, Does.Contain("hunk #1").And.Contain("patch header line 3"));
                Assert.That(exception.Message, Does.Contain("does not match"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_AddDeleteAndMove_ProducesFileLevelPlans()
    {
        var root = CreateTemporaryDirectory();
        var oldPath = Path.Combine(root, "old.txt");
        var deletedPath = Path.Combine(root, "deleted.txt");
        await File.WriteAllTextAsync(oldPath, "old\n");
        await File.WriteAllTextAsync(deletedPath, "delete\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Add File: added.txt
                +added
                *** Delete File: deleted.txt
                *** Update File: old.txt
                *** Move to: moved.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);

            Assert.Multiple(() =>
            {
                Assert.That(plan.Files, Has.Count.EqualTo(3));
                Assert.That(plan.Files[0], Is.TypeOf<PatchAddPlanFile>());
                Assert.That(plan.Files[0].ProposedContent, Is.EqualTo("added"));
                Assert.That(plan.Files[1], Is.TypeOf<PatchDeletePlanFile>());
                Assert.That(plan.Files[2], Is.TypeOf<PatchMovePlanFile>());
                Assert.That(((PatchMovePlanFile)plan.Files[2]).DestinationPath, Is.EqualTo(ResolveFinalPath(Path.Combine(root, "moved.txt"))));
                Assert.That(plan.Files[2].ProposedContent, Is.EqualTo("new\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CreateDifference_AcceptAll_ProducesPlannedContent()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        await File.WriteAllTextAsync(path, "old\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();
            using var difference = file.CreateDifference();
            difference.AcceptAll();

            Assert.That(difference.Apply(file.Original.Content), Is.EqualTo(file.ProposedContent));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void EnsureOutputBudget_MultipleSeparatedChanges_CountsCompleteSynchronousDiff()
    {
        const string original = "old one\ncontext\nold two\n";
        const string proposed = "new one\ncontext\nnew two\n";
        var limits = PatchLimits.Default with { MaxChangedLines = 1 };

        var exception = Assert.Throws<PatchPlanException>(() => PatchPlanBuilder.EnsureOutputBudget(
            "file.txt",
            original,
            proposed,
            Encoding.UTF8.GetByteCount(proposed),
            limits));

        Assert.That(exception!.Message, Does.Contain("changes 2 lines"));
    }

    [Test]
    public async Task BuildAsync_UpdateFinalLink_PlansResolvedReferent()
    {
        var root = CreateTemporaryDirectory();
        var targetPath = Path.Combine(root, "target.txt");
        var linkPath = Path.Combine(root, "link.txt");
        await File.WriteAllTextAsync(targetPath, "old\n");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "target.txt");
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: link.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);
            var update = (PatchUpdatePlanFile)plan.Files.Single();

            Assert.Multiple(() =>
            {
                Assert.That(update.RequestedSourcePath, Is.EqualTo("link.txt"));
                Assert.That(update.SourcePath, Is.EqualTo(ResolveFinalPath(targetPath)));
                Assert.That(update.ReviewPath, Is.EqualTo(ResolveFinalPath(targetPath)));
                Assert.That(
                    update.SourceResolution.LinkTransitions.Select(static transition => transition.Path),
                    Does.Contain(ResolveFinalPath(linkPath, PathResolutionMode.PreserveFinalComponent)));
                Assert.That(update.ProposedContent, Is.EqualTo("new\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_AddThroughParentLink_PlansResolvedDestination()
    {
        var root = CreateTemporaryDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var linkDirectory = Path.Combine(root, "link");
        Directory.CreateDirectory(targetDirectory);

        try
        {
            CreateDirectoryLinkOrIgnore(linkDirectory, "target");
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Add File: link/new.txt
                +content
                *** End Patch
                """,
                root);

            Assert.That(plan.Files.Single().SourcePath, Is.EqualTo(ResolveFinalPath(Path.Combine(targetDirectory, "new.txt"))));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_PathWithLinkThenParentTraversal_UsesPatchLexicalSemantics()
    {
        var root = CreateTemporaryDirectory();
        var targetDirectory = Path.Combine(root, "target");
        var nestedDirectory = Path.Combine(targetDirectory, "nested");
        var lexicalTargetPath = Path.Combine(root, "file.txt");
        var fileSystemOrderTargetPath = Path.Combine(targetDirectory, "file.txt");
        var linkDirectory = Path.Combine(root, "link");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(fileSystemOrderTargetPath, "not selected\n");

        try
        {
            CreateDirectoryLinkOrIgnore(linkDirectory, Path.Combine("target", "nested"));
            var requestedPath = Path.Combine(linkDirectory, "..", "file.txt");
            var plan = await BuildAsync(
                $"""
                *** Begin Patch
                *** Add File: {requestedPath}
                +new
                *** End Patch
                """,
                root);
            var add = (PatchAddPlanFile)plan.Files.Single();

            Assert.Multiple(() =>
            {
                Assert.That(add.RequestedSourcePath, Is.EqualTo(requestedPath));
                Assert.That(add.SourceResolution.RequestedPath, Is.EqualTo(Path.GetFullPath(requestedPath)));
                Assert.That(add.SourcePath, Is.EqualTo(ResolveFinalPath(lexicalTargetPath)));
                Assert.That(
                    add.SourceResolution.LinkTransitions.Any(transition =>
                        string.Equals(
                            transition.Path,
                            ResolveFinalPath(linkDirectory, PathResolutionMode.PreserveFinalComponent),
                            PathUtilities.SystemPathComparison)),
                    Is.False);
                Assert.That(add.ProposedContent, Is.EqualTo("new"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void BuildAsync_LinkTargetWithMissingThenParentTraversal_ReportsMissingComponent()
    {
        var root = CreateTemporaryDirectory();
        var targetPath = Path.Combine(root, "target.txt");
        var linkPath = Path.Combine(root, "link.txt");
        File.WriteAllText(targetPath, "old\n");

        try
        {
            CreateFileLinkOrIgnore(linkPath, Path.Combine("missing", "..", "target.txt"));
            var exception = Assert.ThrowsAsync<PatchPlanException>(async () => await BuildAsync(
                """
                *** Begin Patch
                *** Update File: link.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain(nameof(PathResolutionFailureKind.MissingComponent)));
                Assert.That(exception.Message, Does.Contain(Path.Combine(ResolveFinalPath(root), "missing")));
                Assert.That(exception.Message, Does.Contain("Links followed:").And.Contain("link.txt"));
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("old\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void BuildAsync_DifferentAliasesToSameTarget_RejectsAmbiguousPlan()
    {
        var root = CreateTemporaryDirectory();
        var targetPath = Path.Combine(root, "target.txt");
        var firstLink = Path.Combine(root, "first.txt");
        var secondLink = Path.Combine(root, "second.txt");
        File.WriteAllText(targetPath, "old\n");

        try
        {
            CreateFileLinkOrIgnore(firstLink, "target.txt");
            CreateFileLinkOrIgnore(secondLink, "target.txt");
            var exception = Assert.ThrowsAsync<PatchPlanException>(async () => await BuildAsync(
                """
                *** Begin Patch
                *** Update File: first.txt
                @@
                -old
                +first
                *** Update File: second.txt
                @@
                -old
                +second
                *** End Patch
                """,
                root));

            Assert.That(exception!.Message, Does.Contain("more than one operation").And.Contain(ResolveFinalPath(targetPath)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void BuildAsync_DanglingDestinationLink_TreatsEntryAsOccupied()
    {
        var root = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(root, "source.txt");
        var destinationPath = Path.Combine(root, "destination.txt");
        File.WriteAllText(sourcePath, "content\n");

        try
        {
            CreateFileLinkOrIgnore(destinationPath, "missing.txt");
            var exception = Assert.ThrowsAsync<PatchPlanException>(async () => await BuildAsync(
                """
                *** Begin Patch
                *** Update File: source.txt
                *** Move to: destination.txt
                *** End Patch
                """,
                root));

            Assert.That(exception!.Message,
                Does.Contain("occupied by a link").And.Contain("destination.txt").And.Contain("missing.txt"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task BuildAsync_DanglingDeleteLink_DoesNotReadReferent()
    {
        var root = CreateTemporaryDirectory();
        var linkPath = Path.Combine(root, "link.txt");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "missing.txt");
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Delete File: link.txt
                *** End Patch
                """,
                root);
            var delete = (PatchDeletePlanFile)plan.Files.Single();

            Assert.Multiple(() =>
            {
                Assert.That(delete.SourcePath, Is.EqualTo(ResolveFinalPath(linkPath, PathResolutionMode.PreserveFinalComponent)));
                Assert.That(delete.SourceLink?.Target, Is.EqualTo("missing.txt"));
                Assert.That(delete.Original.Exists, Is.False);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public void BuildAsync_DanglingUpdateLink_ReportsOperationLinkAndTarget()
    {
        var root = CreateTemporaryDirectory();
        var linkPath = Path.Combine(root, "link.txt");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "missing.txt");
            var exception = Assert.ThrowsAsync<PatchPlanException>(async () => await BuildAsync(
                """
                *** Begin Patch
                *** Update File: link.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain("Update File 'link.txt' (patch header line 2)"));
                Assert.That(exception.Message, Does.Contain("Links followed:"));
                Assert.That(exception.Message, Does.Contain("link.txt").And.Contain("missing.txt"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static async Task<PatchPlan> BuildAsync(string patch, string root)
    {
        var document = PatchParser.Parse(patch);
        return await PatchPlanBuilder.BuildAsync(document, root, PatchLimits.Default, CancellationToken.None);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "everywhere-patch-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveFinalPath(string path)
    {
        var result = PathUtilities.ResolvePath(path, PathResolutionMode.FollowFinalComponent);
        return result.ResolvedPath ?? throw new AssertionException($"Could not resolve test path '{path}'.");
    }

    private static string ResolveFinalPath(string path, PathResolutionMode mode)
    {
        var result = PathUtilities.ResolvePath(path, mode);
        return result.ResolvedPath ?? throw new AssertionException($"Could not resolve test path '{path}'.");
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

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
