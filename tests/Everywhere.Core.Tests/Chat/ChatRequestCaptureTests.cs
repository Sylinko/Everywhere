using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.I18N;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatRequestCaptureTests
{
    [Test]
    public async Task BuildSelectedHistory_WhenFilesAreAttached_SendsOnlyMetadataEvenForSupportedModalities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Everywhere-request-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var file = new FileAttachment(new DirectLocaleKey("image"), path, "test-image", "image/png", "from clipboard");
            var user = new UserChatMessage("Compare my image and the file from earlier.", [file]);
            var legacyCall = new FunctionCallContent("read_file", id: "old-read");
            var legacyTool = new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("read_file"));
            legacyTool.AddCall(legacyCall);
            legacyTool.AddResult(new FunctionResultContent(legacyCall, file));
            using var assistant = new AssistantChatMessage();
            assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(legacyTool));

            // The legacy read_file result used to re-inject bytes whenever this chat was resumed.
            var history = await ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
                Substitute.For<IPromptRenderer>(), "system", [assistant, user], Modalities.Text | Modalities.Image);
            var result = history.Single(message => message.Role == AuthorRole.Tool).Items.OfType<FunctionResultContent>().Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(history.SelectMany(message => message.Items).OfType<BinaryContent>(), Is.Empty);
                Assert.That(result.Result, Does.Contain(path).And.Contain("mimeType=\"image/png\""));
                Assert.That(history[^1].Items.OfType<TextContent>().First().Text,
                    Does.Contain(path).And.Contain("description=\"from clipboard\""));
                Assert.That(history[^1].Items.OfType<TextContent>().Last().Text, Is.EqualTo(user.Content));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task BuildSelectedHistory_WhenToolArgumentsChange_DoesNotRetainMutableCallContainers()
    {
        var arguments = new KernelArguments { ["path"] = "before.txt" };
        var call = new FunctionCallContent("read", "files", "call-1", arguments);
        var metadata = new Dictionary<string, object?> { ["source"] = "before" };
        var result = new FunctionResultContent(call, "result") { Metadata = metadata };
        using var assistant = new AssistantChatMessage();
        var function = new FunctionCallChatMessage(LucideIconKind.File, new DirectLocaleKey("read"));
        function.AddCall(call);
        function.AddResult(result);
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(function));

        var data = new ChatData();
        data.Add(assistant);
        await using var runtime = new RootChatContext(data, null);
        var history = await ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
            Substitute.For<IPromptRenderer>(), "system", [assistant], Modalities.Text, context: runtime);
        await runtime.DispatchAsync(() =>
        {
            arguments["path"] = "after.txt";
            metadata["source"] = "after";
        });
        var capturedCall = history.Single(message => message.Role == AuthorRole.Assistant).Items.OfType<FunctionCallContent>().Single();
        var capturedResult = history.Single(message => message.Role == AuthorRole.Tool).Items.OfType<FunctionResultContent>().Single();
        Assert.That(capturedCall.Arguments?["path"], Is.EqualTo("before.txt"));
        Assert.That(capturedResult.Metadata?["source"], Is.EqualTo("before"));
    }

    [Test]
    public async Task BuildSelectedHistory_WhenCanceled_DoesNotTurnCancellationIntoAnAttachmentOmission()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var user = new UserChatMessage("message", [new FileAttachment(new DirectLocaleKey("file"), "missing.png", "missing", "image/png")]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
            Substitute.For<IPromptRenderer>(), "system", [user], Modalities.Image, cancellation.Token));
    }
}