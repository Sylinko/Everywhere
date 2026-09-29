using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;
using Everywhere.Common;

namespace Everywhere.Core.Tests.Chat;

public class PatchReviewSessionTests
{
    [Test]
    public async Task ReviewAsync_MultipleFiles_RequestsConsentBeforeAppendingCompletedSummaries()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "one\n");
        await File.WriteAllTextAsync(Path.Combine(root, "two.txt"), "two\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: one.txt
                @@
                -one
                +ONE
                *** Update File: two.txt
                @@
                -two
                +TWO
                *** End Patch
                """,
                root);
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();
            var callbackCount = 0;

            var decisions = await session.ReviewAsync(
                (item, _) =>
                {
                    Assert.That(sink, Has.Count.EqualTo(callbackCount));
                    Assert.Multiple(() =>
                    {
                        Assert.That(item.DisplayBlock.CanReview, Is.True);
                        Assert.That(item.DisplayBlock.Difference, Is.SameAs(item.Difference));
                    });
                    callbackCount++;
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.EqualTo(2));
                Assert.That(decisions, Has.All.TypeOf<PatchContentFileDecision>());
                Assert.That(
                    decisions.Cast<PatchContentFileDecision>().Select(static decision => decision.Content),
                    Is.EqualTo(["ONE\n", "TWO\n"]));
                Assert.That(sink, Has.Count.EqualTo(2));
            });
            foreach (var block in sink.OfType<ChatPluginFileDifferenceDisplayBlock>())
            {
                Assert.Multiple(() =>
                {
                    Assert.That(block.CanReview, Is.False);
                    Assert.That(block.Difference, Is.Null);
                    Assert.That(block.OriginalText, Is.Null);
                });
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_MultipleHunksInOneFile_UsesSingleConsent()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "one\nmiddle\ntwo\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -one
                +ONE
                @@
                -two
                +TWO
                *** End Patch
                """,
                root);
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();
            var callbackCount = 0;

            var decisions = await session.ReviewAsync(
                (item, _) =>
                {
                    callbackCount++;
                    Assert.That(item.File.ReviewPath, Is.EqualTo(ResolveFinalPath(Path.Combine(root, "file.txt"))));
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.EqualTo(1));
                Assert.That(((PatchContentFileDecision)decisions.Single()).Content, Is.EqualTo("ONE\nmiddle\nTWO\n"));
                Assert.That(sink, Has.Count.EqualTo(1));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_NoRejectedChanges_UsesAuthoritativePlannedContent()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "old\n");

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
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();

            var decisions = await session.ReviewAsync(
                (item, _) =>
                {
                    item.Difference.AddRange(TextChange.Insert(0, "incorrect diff projection\n"));
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);

            Assert.That(((PatchContentFileDecision)decisions.Single()).Content, Is.EqualTo("new\n"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_SomeChangesRejected_AppliesOnlyAcceptedDiffBlocks()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "one\nmiddle\ntwo\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -one
                +ONE
                @@
                -two
                +TWO
                *** End Patch
                """,
                root);
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();

            var decisions = await session.ReviewAsync(
                (item, _) =>
                {
                    item.Difference.GetFilteredChanges(default).Last().IsAccepted = false;
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);

            Assert.That(((PatchContentFileDecision)decisions.Single()).Content, Is.EqualTo("ONE\nmiddle\ntwo\n"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_MoveOnly_UsesFileLevelConsentAndMoveSummary()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "content\n");

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
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();

            var decisions = await session.ReviewAsync(
                (item, _) =>
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(item.File, Is.TypeOf<PatchMovePlanFile>());
                        Assert.That(item.DisplayBlock.ReviewKind, Is.EqualTo(TextDifferenceReviewKind.Move));
                        Assert.That(item.DisplayBlock.CanReview, Is.False);
                    });
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(decisions.Single(), Is.TypeOf<PatchContentFileDecision>());
                Assert.That(((PatchContentFileDecision)decisions.Single()).Content, Is.EqualTo("content\n"));
                Assert.That(
                    ((ChatPluginFileDifferenceDisplayBlock)sink.Single()).ReviewKind,
                    Is.EqualTo(TextDifferenceReviewKind.Move));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_ContentOperations_AllUseFileLevelConsent()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "removed.txt"), "remove me\n");
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "old\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Add File: added.txt
                +new file
                *** Delete File: removed.txt
                *** Update File: old.txt
                *** Move to: moved.txt
                @@
                -old
                +new
                *** End Patch
                """,
                root);
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();
            var callbackCount = 0;

            var decisions = await session.ReviewAsync(
                (_, _) =>
                {
                    callbackCount++;
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(callbackCount, Is.EqualTo(3));
                Assert.That(
                    sink.OfType<ChatPluginFileDifferenceDisplayBlock>().Select(static block => block.ReviewKind),
                    Is.EqualTo([
                        TextDifferenceReviewKind.Create,
                        TextDifferenceReviewKind.Delete,
                        TextDifferenceReviewKind.MoveAndUpdate
                    ]));
                Assert.That(decisions[0], Is.TypeOf<PatchContentFileDecision>());
                Assert.That(decisions[1], Is.TypeOf<PatchDeleteFileDecision>());
                Assert.That(decisions[2], Is.TypeOf<PatchContentFileDecision>());
                Assert.That(((PatchContentFileDecision)decisions[0]).Content, Is.EqualTo("new file"));
                Assert.That(((PatchContentFileDecision)decisions[2]).Content, Is.EqualTo("new\n"));
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_RejectedWithReason_PreservesUserIntentAndCompletedSummary()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "content\n");

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
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();

            var decisions = await session.ReviewAsync(
                static (_, _) => Task.FromResult(RequestConsentResult.Deny("Keep the original path.")),
                sink,
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(((PatchRejectedFileDecision)decisions.Single()).Reason, Is.EqualTo("Keep the original path."));
                Assert.That(sink, Has.Count.EqualTo(1));
                Assert.That(((ChatPluginFileDifferenceDisplayBlock)sink.Single()).CanReview, Is.False);
            });
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Test]
    public async Task ReviewAsync_AllChangesRejected_PreservesRejectionAndComment()
    {
        var root = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "one\nmiddle\ntwo\n");

        try
        {
            var plan = await BuildAsync(
                """
                *** Begin Patch
                *** Update File: file.txt
                @@
                -one
                +ONE
                @@
                -two
                +TWO
                *** End Patch
                """,
                root);
            using var session = PatchReviewSession.Create(plan);
            using var sink = new ChatPluginDisplaySink();

            var decisions = await session.ReviewAsync(
                (item, _) =>
                {
                    item.Difference.RejectAll();
                    var rejectedChange = item.Difference.GetFilteredChanges(default).First();
                    rejectedChange.ReviewComment = "Keep this wording.";
                    return Task.FromResult(RequestConsentResult.Accept);
                },
                sink,
                CancellationToken.None);
            var decision = (PatchRejectedFileDecision)decisions.Single();

            Assert.Multiple(() =>
            {
                Assert.That(decision.Changes.Count(static change => change.Accepted), Is.Zero);
                Assert.That(decision.Changes.Count(static change => !change.Accepted), Is.EqualTo(2));
                Assert.That(decision.Changes.Count(static change => change.Comment == "Keep this wording."), Is.EqualTo(1));
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
        var path = Path.Combine(Path.GetTempPath(), "everywhere-patch-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveFinalPath(string path)
    {
        var result = PathUtilities.ResolvePath(path, PathResolutionMode.FollowFinalComponent);
        return result.ResolvedPath ?? throw new AssertionException($"Could not resolve test path '{path}'.");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
