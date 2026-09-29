using Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

namespace Everywhere.Core.Tests.Chat;

public class PatchCommitterTests
{
    [Test]
    public async Task CommitAsync_Update_WritesDirectlyAndPreservesBom()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "file.txt");
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllBytesAsync(
            path,
            encoding.GetPreamble().Concat(encoding.GetBytes("old\r\n")).ToArray());

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
            Assert.That(file, Is.TypeOf<PatchUpdatePlanFile>());
            var update = (PatchUpdatePlanFile)file;
            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.Files.Single().Status, Is.EqualTo(PatchCommitStatus.Committed));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(update.ProposedBytes));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_WhenSourceChangesAfterPlanning_LeavesExternalContentUntouched()
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
            await File.WriteAllTextAsync(path, "external\n");
            var file = plan.Files.Single();

            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Files.Single().Status, Is.EqualTo(PatchCommitStatus.Conflict));
                Assert.That(File.ReadAllText(path), Is.EqualTo("external\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_MultiFileAddDeleteMove_CommitsAllAcceptedOperations()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "old\n");
        await File.WriteAllTextAsync(Path.Combine(root, "deleted.txt"), "deleted\n");

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
            var decisions = plan.Files.Select(file => file switch
            {
                PatchDeletePlanFile => (PatchFileDecision)new PatchDeleteFileDecision(file.SourcePath, []),
                _ => new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])
            }).ToArray();

            var result = await PatchCommitter.CommitAsync(plan, decisions, PatchLimits.Default, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(File.ReadAllText(Path.Combine(root, "added.txt")), Is.EqualTo("added"));
                Assert.That(File.Exists(Path.Combine(root, "deleted.txt")), Is.False);
                Assert.That(File.Exists(Path.Combine(root, "old.txt")), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(root, "moved.txt")), Is.EqualTo("new\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_PureMove_PreservesOriginalBytes()
    {
        var root = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(root, "old.txt");
        var destinationPath = Path.Combine(root, "new.txt");
        var originalBytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            .GetBytes("content\r\n");
        await File.WriteAllBytesAsync(sourcePath, [0xEF, 0xBB, 0xBF, .. originalBytes]);

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: old.txt
                *** Move to: new.txt
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();
            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(File.Exists(sourcePath), Is.False);
                Assert.That(File.ReadAllBytes(destinationPath), Is.EqualTo([0xEF, 0xBB, 0xBF, .. originalBytes]));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_RejectedDecision_SkipsMutation()
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

            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchRejectedFileDecision(file.SourcePath, "Keep the current implementation.", [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.Files.Single().Status, Is.EqualTo(PatchCommitStatus.RejectedByUser));
                Assert.That(
                    ((PatchRejectedFileDecision)result.Files.Single().Decision).Reason,
                    Is.EqualTo("Keep the current implementation."));
                Assert.That(File.ReadAllText(path), Is.EqualTo("old\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_UpdateFinalLink_UpdatesReferentAndPreservesLink()
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
            var file = plan.Files.Single();

            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("new\n"));
                Assert.That(new FileInfo(linkPath).LinkTarget, Is.EqualTo("target.txt"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CommitAsync_DeleteFinalLink_RemovesEntryAndPreservesReferent(bool isDangling)
    {
        var root = CreateTemporaryDirectory();
        var targetPath = Path.Combine(root, "target.txt");
        var linkPath = Path.Combine(root, "link.txt");
        if (!isDangling) await File.WriteAllTextAsync(targetPath, "content\n");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "target.txt");
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Delete File: link.txt
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();

            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchDeleteFileDecision(file.SourcePath, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(new FileInfo(linkPath).LinkTarget, Is.Null);
                Assert.That(File.Exists(targetPath), Is.EqualTo(!isDangling));
                if (!isDangling) Assert.That(File.ReadAllText(targetPath), Is.EqualTo("content\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_MoveFinalLink_WritesRegularDestinationAndPreservesReferent()
    {
        var root = CreateTemporaryDirectory();
        var targetPath = Path.Combine(root, "target.txt");
        var linkPath = Path.Combine(root, "link.txt");
        var destinationPath = Path.Combine(root, "moved.txt");
        await File.WriteAllTextAsync(targetPath, "old\n");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "target.txt");
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: link.txt
                *** Move to: moved.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);
            var file = plan.Files.Single();

            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.True);
                Assert.That(new FileInfo(linkPath).LinkTarget, Is.Null);
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("old\n"));
                Assert.That(File.ReadAllText(destinationPath), Is.EqualTo("new\n"));
                Assert.That(new FileInfo(destinationPath).LinkTarget, Is.Null);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task CommitAsync_WhenLinkTargetChangesAfterPlanning_ReturnsConflictWithoutWriting()
    {
        var root = CreateTemporaryDirectory();
        var firstTargetPath = Path.Combine(root, "first.txt");
        var secondTargetPath = Path.Combine(root, "second.txt");
        var linkPath = Path.Combine(root, "link.txt");
        await File.WriteAllTextAsync(firstTargetPath, "old\n");
        await File.WriteAllTextAsync(secondTargetPath, "old\n");

        try
        {
            CreateFileLinkOrIgnore(linkPath, "first.txt");
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
            File.Delete(linkPath);
            CreateFileLinkOrIgnore(linkPath, "second.txt");
            var file = plan.Files.Single();

            var result = await PatchCommitter.CommitAsync(
                plan,
                [new PatchContentFileDecision(file.SourcePath, file.ProposedContent, [])],
                PatchLimits.Default,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Files.Single().Status, Is.EqualTo(PatchCommitStatus.Conflict));
                Assert.That(result.Error, Does.Contain("resolves differently"));
                Assert.That(File.ReadAllText(firstTargetPath), Is.EqualTo("old\n"));
                Assert.That(File.ReadAllText(secondTargetPath), Is.EqualTo("old\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static async Task<PatchPlan> BuildAsync(string patch, string root) =>
        await PatchPlanBuilder.BuildAsync(PatchParser.Parse(patch), root, PatchLimits.Default, CancellationToken.None);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "everywhere-patch-committer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
