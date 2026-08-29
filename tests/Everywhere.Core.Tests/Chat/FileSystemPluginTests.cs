using System.Reflection;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Prompting.Documents;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace Everywhere.Core.Tests.Chat;

public class FileSystemPluginTests
{
    [TestCase("Copy", true)]
    [TestCase("Move", false)]
    public async Task TransferPathAsync_ForFile_PerformsRequestedOperation(string operation, bool sourceShouldRemain)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "nested", "destination.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "content");

        try
        {
            var plugin = CreatePlugin();
            var userInterface = CreateUserInterface(consent: true);

            await InvokeTransferPathAsync(plugin, userInterface, source, destination, operation);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(source), Is.EqualTo(sourceShouldRemain));
                Assert.That(File.ReadAllText(destination), Is.EqualTo("content"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TransferPathAsync_CopyDirectory_CopiesNestedContentAndKeepsSource()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        var sourceFile = Path.Combine(source, "nested", "content.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "content");

        try
        {
            var plugin = CreatePlugin();
            var userInterface = CreateUserInterface(consent: true);

            await InvokeTransferPathAsync(plugin, userInterface, source, destination, "Copy");

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(source), Is.True);
                Assert.That(
                    File.ReadAllText(Path.Combine(destination, "nested", "content.txt")),
                    Is.EqualTo("content"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_UpdateReviewsAndCommitsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "old\n");

        try
        {
            var displaySink = new ChatPluginDisplaySink();
            var consentSawReview = false;
            var userInterface = CreateUserInterface(
                consent: true,
                displaySink,
                content =>
                {
                    Assert.That(displaySink, Is.Empty);
                    Assert.That(content, Is.TypeOf<ChatPluginFileDifferenceDisplayBlock>());
                    if (content is not ChatPluginFileDifferenceDisplayBlock block)
                        throw new AssertionException("Consent content is not a file-difference block.");
                    Assert.Multiple(() =>
                    {
                        Assert.That(block.CanReview, Is.True);
                        Assert.That(block.Difference, Is.Not.Null);
                    });
                    consentSawReview = true;
                });
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -old
                +new
                *** End Patch
                """;
            var result = await InvokeApplyPatchAsync(CreatePlugin(), userInterface, patch);
            var completedBlock = displaySink.OfType<ChatPluginFileDifferenceDisplayBlock>().Single();

            Assert.Multiple(() =>
            {
                Assert.That(consentSawReview, Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("new\n"));
                Assert.That(result, Does.Contain("Patch applied successfully."));
                Assert.That(result, Does.Contain("Applied line changes: 1 line added, 1 line deleted."));
                Assert.That(result, Does.Contain("committed:").And.Contain("1 line added, 1 line deleted"));
                Assert.That(result, Does.Not.Contain("Warnings: tolerant matching"));
                Assert.That(completedBlock.CanReview, Is.False);
                Assert.That(completedBlock.Difference, Is.Null);
                Assert.That(completedBlock.OriginalText, Is.Null);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_RejectedChange_ReportsUserCommentWithoutChangingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "old\n");

        try
        {
            var displaySink = new ChatPluginDisplaySink();
            var userInterface = CreateUserInterface(
                consent: true,
                displaySink,
                content =>
                {
                    Assert.That(content, Is.TypeOf<ChatPluginFileDifferenceDisplayBlock>());
                    if (content is not ChatPluginFileDifferenceDisplayBlock block)
                        throw new AssertionException("Consent content is not a file-difference block.");
                    var difference = block.Difference ?? throw new AssertionException("Consent content has no review difference.");
                    var change = difference.GetFilteredChanges(default).Single();
                    change.IsAccepted = false;
                    change.ReviewComment = "Keep the existing behavior.";
                });
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -old
                +new
                *** End Patch
                """;
            var result = await InvokeApplyPatchAsync(CreatePlugin(), userInterface, patch);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("old\n"));
                Assert.That(
                    result,
                    Does.Contain("The patch parsed and matched successfully; the proposed changes were rejected during user review."));
                Assert.That(result, Does.Contain("review-generated text changes, not patch hunks"));
                Assert.That(result, Does.Contain("review-generated changes: 0 accepted, 1 rejected"));
                Assert.That(result, Does.Contain("Applied line changes: 0 lines added, 0 lines deleted."));
                Assert.That(result, Does.Contain("Do not retry rejected changes unless the user requests it."));
                Assert.That(result, Does.Contain("user comment on rejected change"));
                Assert.That(result, Does.Contain("Keep the existing behavior."));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestCase("old  \n", "old\t", "after ignoring trailing whitespace")]
    [TestCase("    old\n", "old", "after ignoring leading and trailing whitespace")]
    [TestCase("message = “hello”\n", "message = \"hello\"", "after normalizing compatible Unicode whitespace or punctuation")]
    public async Task ApplyPatchAsync_FallbackMatch_ReportsSuccessfulWarning(
        string original,
        string removedLine,
        string expectedWarning)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, original);

        try
        {
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -{removedLine}
                +new
                *** End Patch
                """;

            var result = await InvokeApplyPatchAsync(CreatePlugin(), CreateUserInterface(consent: true), patch);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("new\n"));
                Assert.That(result, Does.Contain("Warnings: tolerant matching was used while planning this patch:"));
                Assert.That(result, Does.Contain($"committed: {path}, hunk #1 (patch header line 3) matched {expectedWarning}."));
                Assert.That(result, Does.Contain("Added lines were kept exactly as supplied"));
                Assert.That(result, Does.Contain("re-read the affected lines and verify indentation and intended text"));
                Assert.That(result, Does.Contain("successful fallback matches, not patch errors").And.Contain("Do not retry automatically"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_RejectedFallbackMatch_DoesNotClaimThatItWasWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "    old\n");

        try
        {
            var displaySink = new ChatPluginDisplaySink();
            var userInterface = CreateUserInterface(
                consent: true,
                displaySink,
                content =>
                {
                    var block = content as ChatPluginFileDifferenceDisplayBlock ??
                        throw new AssertionException("Consent content is not a file-difference block.");
                    var change = block.Difference?.GetFilteredChanges(default).Single() ??
                        throw new AssertionException("Consent content has no review change.");
                    change.IsAccepted = false;
                });
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -old
                +new
                *** End Patch
                """;

            var result = await InvokeApplyPatchAsync(CreatePlugin(), userInterface, patch);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("    old\n"));
                Assert.That(result, Does.Contain($"rejected by user: {path}, hunk #1"));
                Assert.That(result, Does.Contain("For committed files, re-read"));
                Assert.That(result, Does.Not.Contain($"committed: {path}, hunk #1"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_PartiallyRejectedChanges_ReportsReviewSemanticsAndCommitsAcceptedChange()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "old first\nkeep 1\nkeep 2\nkeep 3\nkeep 4\nold second\n");

        try
        {
            var displaySink = new ChatPluginDisplaySink();
            var userInterface = CreateUserInterface(
                consent: true,
                displaySink,
                content =>
                {
                    Assert.That(content, Is.TypeOf<ChatPluginFileDifferenceDisplayBlock>());
                    if (content is not ChatPluginFileDifferenceDisplayBlock block)
                        throw new AssertionException("Consent content is not a file-difference block.");
                    var difference = block.Difference ?? throw new AssertionException("Consent content has no review difference.");
                    var changes = difference.GetFilteredChanges(default).ToArray();
                    Assert.That(changes, Has.Length.EqualTo(2));
                    changes[1].IsAccepted = false;
                });
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -old first
                +new first
                @@
                -old second
                +new second
                *** End Patch
                """;

            var result = await InvokeApplyPatchAsync(CreatePlugin(), userInterface, patch);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("new first\nkeep 1\nkeep 2\nkeep 3\nkeep 4\nold second\n"));
                Assert.That(
                    result,
                    Does.Contain("The patch parsed and matched successfully; some proposed changes were rejected during user review."));
                Assert.That(result, Does.Contain("review-generated changes: 1 accepted, 1 rejected"));
                Assert.That(result, Does.Contain("Applied line changes: 1 line added, 1 line deleted."));
                Assert.That(result, Does.Contain("Do not retry rejected changes unless the user requests it."));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_ContextHunk_UpdatesOnlyTheAnchoredOccurrence()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "method A\nold\nmethod B\nold\n");

        try
        {
            var displaySink = new ChatPluginDisplaySink();
            var userInterface = CreateUserInterface(consent: true, displaySink);
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@ method B
                -old
                +new
                *** End Patch
                """;
            await InvokeApplyPatchAsync(CreatePlugin(), userInterface, patch);

            Assert.That(File.ReadAllText(path), Is.EqualTo("method A\nold\nmethod B\nnew\n"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_ApprovedPath_SkipsConsentAndPublishesCompletedSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "old\n");

        try
        {
            var settings = new Settings(Substitute.For<IServiceProvider>());
            settings.Plugin.FileSystem.AddApprovalPath(path);
            var displaySink = new ChatPluginDisplaySink();
            var userInterface = CreateUserInterface(consent: true, displaySink);
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -old
                +new
                *** End Patch
                """;

            await InvokeApplyPatchAsync(CreatePlugin(settings), userInterface, patch);

            await userInterface.DidNotReceive().RequestConsentAsync(
                Arg.Any<string?>(),
                Arg.Any<IDynamicLocaleKey>(),
                Arg.Any<ChatPluginDisplayBlock?>(),
                Arg.Any<RequestConsentRememberMasks>(),
                Arg.Any<IReadOnlyList<RequestConsentCustomOption>?>(),
                cancellationToken: Arg.Any<CancellationToken>());
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("new\n"));
                Assert.That(displaySink.OfType<ChatPluginFileDifferenceDisplayBlock>().Single().CanReview, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_ParentPathApproval_CoversLaterFilesInSamePatch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "first.txt");
        var secondPath = Path.Combine(root, "second.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(firstPath, "first\n");
        await File.WriteAllTextAsync(secondPath, "second\n");

        try
        {
            var settings = new Settings(Substitute.For<IServiceProvider>());
            var displaySink = new ChatPluginDisplaySink();
            var userInterface = Substitute.For<IChatPluginUserInterface>();
            userInterface.DisplaySink.Returns(displaySink);
            var consentCount = 0;
            userInterface.RequestConsentAsync(
                    Arg.Any<string?>(),
                    Arg.Any<IDynamicLocaleKey>(),
                    Arg.Any<ChatPluginDisplayBlock?>(),
                    Arg.Any<RequestConsentRememberMasks>(),
                    Arg.Any<IReadOnlyList<RequestConsentCustomOption>?>(),
                    cancellationToken: Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    consentCount++;
                    var options = call.ArgAt<IReadOnlyList<RequestConsentCustomOption>?>(4) ??
                        throw new AssertionException("The consent request has no path options.");
                    var parentOption = options.Single(static option => option.HeaderKey is FormattedDynamicLocaleKey);
                    return Task.FromResult(RequestConsentResult.Custom(parentOption));
                });
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(firstPath).AbsoluteUri}
                @@
                -first
                +FIRST
                *** Update File: {new Uri(secondPath).AbsoluteUri}
                @@
                -second
                +SECOND
                *** End Patch
                """;

            await InvokeApplyPatchAsync(CreatePlugin(settings), userInterface, patch);

            Assert.Multiple(() =>
            {
                Assert.That(consentCount, Is.EqualTo(1));
                Assert.That(File.ReadAllText(firstPath), Is.EqualTo("FIRST\n"));
                Assert.That(File.ReadAllText(secondPath), Is.EqualTo("SECOND\n"));
                Assert.That(displaySink.OfType<ChatPluginFileDifferenceDisplayBlock>().ToArray(), Has.Length.EqualTo(2));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ApplyPatchAsync_WorkingDirectoryPath_SkipsConsent()
    {
        var chatContext = new ChatContext();
        var root = Path.Combine(chatContext.EnsureWorkingDirectory(), "patch-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "old\n");

        try
        {
            var userInterface = CreateUserInterface(consent: true);
            var patch = $"""
                *** Begin Patch
                *** Update File: {new Uri(path).AbsoluteUri}
                @@
                -old
                +new
                *** End Patch
                """;

            await InvokeApplyPatchAsync(CreatePlugin(), userInterface, patch, chatContext);

            await userInterface.DidNotReceive().RequestConsentAsync(
                Arg.Any<string?>(),
                Arg.Any<IDynamicLocaleKey>(),
                Arg.Any<ChatPluginDisplayBlock?>(),
                Arg.Any<RequestConsentRememberMasks>(),
                Arg.Any<IReadOnlyList<RequestConsentCustomOption>?>(),
                cancellationToken: Arg.Any<CancellationToken>());
            Assert.That(File.ReadAllText(path), Is.EqualTo("new\n"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            chatContext.Dispose();
        }
    }

    [Test]
    public async Task ApplyPatchAsync_InvalidPatch_LeavesFileUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "original\n");

        try
        {
            var exception = Assert.ThrowsAsync<HandledException>(async () =>
                await InvokeApplyPatchAsync(
                    CreatePlugin(),
                    CreateUserInterface(consent: true),
                    "*** Begin Patch\n*** Update File: source.txt\n*** End Patch"));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain("must contain at least one hunk"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("original\n"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task DeletePathsAsync_NonEmptyDirectoryRequiresRecursive()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "content.txt"), "content");

        try
        {
            var plugin = CreatePlugin();
            var exception = Assert.ThrowsAsync<HandledException>(async () =>
                await InvokeDeletePathsAsync(plugin, CreateUserInterface(consent: true), [root], recursive: false));

            Assert.That(exception!.Message, Does.Contain("not empty"));
            await InvokeDeletePathsAsync(plugin, CreateUserInterface(consent: true), [root], recursive: true);
            Assert.That(Directory.Exists(root), Is.False);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void Constructor_RegistersPatchAndPathTools_WithoutLegacyTextTools()
    {
        var names = CreatePlugin().GetChatFunctions().Select(static function => function.KernelFunction.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("apply_patch"));
            Assert.That(names, Does.Contain("transfer_path"));
            Assert.That(names, Does.Contain("delete_paths"));
            Assert.That(names, Does.Not.Contain("write_to_file"));
            Assert.That(names, Does.Not.Contain("replace_file_content"));
        });
    }

    [Test]
    public void ApplyPatch_Description_StatesCompleteToolProtocol()
    {
        var method = typeof(FileSystemPlugin).GetMethod("ApplyPatchAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var applyMethod = method ?? throw new AssertionException("ApplyPatchAsync was not found.");

        var methodDescription = applyMethod.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var patchDescription = applyMethod
            .GetParameters()
            .Single(static parameter => parameter.Name is "patch")
            .GetCustomAttribute<DescriptionAttribute>()?
            .Description;

        Assert.Multiple(() =>
        {
            Assert.That(methodDescription, Does.Contain("EXACT protocol"));
            Assert.That(methodDescription, Does.Contain("A bare `@@` searches forward for the first matching context/removal lines"));
            Assert.That(methodDescription, Does.Contain("multiple complete `@@` hunks under it"));
            Assert.That(methodDescription, Does.Contain("top-to-bottom source order"));
            Assert.That(methodDescription, Does.Contain("DO NOT use an anchored header unless"));
            Assert.That(methodDescription, Does.Contain("immediately after that exact source line"));
            Assert.That(methodDescription, Does.Contain("cannot itself be modified by that hunk"));
            Assert.That(methodDescription, Does.Contain("The body line starts AFTER the anchor text"));
            Assert.That(methodDescription, Does.Contain("Copy leading whitespace EXACTLY AFTER the marker"));
            Assert.That(methodDescription, Does.Contain("`+` lines are written EXACTLY as supplied"));
            Assert.That(methodDescription, Does.Contain("Every hunk must contain at least one `+` or `-`"));
            Assert.That(methodDescription, Does.Contain("It does not automatically append content to the file"));
            Assert.That(
                patchDescription,
                Is.EqualTo("The complete patch document, including the *** Begin Patch and *** End Patch markers."));
        });
    }

    private static FileSystemPlugin CreatePlugin(Settings? settings = null) =>
        new(
            settings ?? new Settings(Substitute.For<IServiceProvider>()),
            new FileHandlerContextFactory([new PdfFileHandler(), new TextFileHandler(), new BinaryFileHandler()]),
            Substitute.For<ILogger<FileSystemPlugin>>());

    private static IChatPluginUserInterface CreateUserInterface(
        bool consent,
        ChatPluginDisplaySink? displaySink = null,
        Action<ChatPluginDisplayBlock?>? onConsent = null)
    {
        var userInterface = Substitute.For<IChatPluginUserInterface>();
        userInterface.DisplaySink.Returns(displaySink ?? new ChatPluginDisplaySink());
        userInterface.RequestConsentAsync(
                Arg.Any<string?>(),
                Arg.Any<IDynamicLocaleKey>(),
                Arg.Any<ChatPluginDisplayBlock?>(),
                Arg.Any<RequestConsentRememberMasks>(),
                Arg.Any<IReadOnlyList<RequestConsentCustomOption>?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onConsent?.Invoke(call.ArgAt<ChatPluginDisplayBlock?>(2));
                return Task.FromResult(new RequestConsentResult(consent, consent ? null : "denied"));
            });
        return userInterface;
    }

    private static async Task InvokeTransferPathAsync(
        FileSystemPlugin plugin,
        IChatPluginUserInterface userInterface,
        string source,
        string destination,
        string operation)
    {
        var method = typeof(FileSystemPlugin).GetMethod(
            "TransferPathAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var operationType = method!
            .GetParameters()
            .Single(static parameter => parameter.Name is "operation")
            .ParameterType;
        var operationValue = Enum.Parse(operationType, operation);
        var task = (Task)method.Invoke(
            plugin,
            [
                userInterface,
                new ChatContext(),
                source,
                destination,
                operationValue,
                CancellationToken.None
            ])!;
        await task;
    }

    private static async Task<string> InvokeApplyPatchAsync(
        FileSystemPlugin plugin,
        IChatPluginUserInterface userInterface,
        string patch,
        ChatContext? chatContext = null)
    {
        var method = typeof(FileSystemPlugin).GetMethod(
            "ApplyPatchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var applyPatchMethod = method ?? throw new AssertionException("ApplyPatchAsync was not found.");
        var invocation = applyPatchMethod.Invoke(
            plugin,
            [userInterface, chatContext ?? new ChatContext(), patch, CancellationToken.None]);
        if (invocation is not Task<PromptNode> task)
            throw new AssertionException("ApplyPatchAsync did not return Task<PromptNode>.");

        return (await task).ToString();
    }

    private static async Task InvokeDeletePathsAsync(
        FileSystemPlugin plugin,
        IChatPluginUserInterface userInterface,
        IReadOnlyList<string> paths,
        bool recursive)
    {
        var method = typeof(FileSystemPlugin).GetMethod(
            "DeletePathsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var task = (Task<string>)method!.Invoke(
            plugin,
            [userInterface, new ChatContext(), paths, recursive, CancellationToken.None])!;
        await task;
    }

}
