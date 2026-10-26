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
    public async Task BuildSelectedHistory_WhenModelChangesDuringFileRead_UsesCapturedTextAndFileValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "Everywhere-request-" + Guid.NewGuid() + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[8 * 1024 * 1024]);
            var file = new FileAttachment(new DirectLocaleKey("image"), path, "test-image", "image/png", "before");
            var user = new UserChatMessage("before user", [file]);
            using var assistant = new AssistantChatMessage();
            var text = new AssistantChatMessageTextSpan("before assistant");
            assistant.AddSpan(text);

            var pending = ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
                Substitute.For<IPromptRenderer>(), "system", [user, assistant], Modalities.Text | Modalities.Image);
            user.Content = "after user";
            file.FilePath = "missing-after.png";
            file.Description = "after";
            text.ContentMarkdownBuilder.Append(" after assistant");
            var history = await pending;

            Assert.That(history[1].Items.OfType<ImageContent>().Single().Data?.Length, Is.EqualTo(8 * 1024 * 1024));
            Assert.That(history[1].Items.OfType<TextContent>().Select(item => item.Text), Has.Some.EqualTo("before user"));
            Assert.That(history[1].Items.OfType<TextContent>().First().Text, Does.Contain("description=\"before\""));
            Assert.That(history[2].Content, Is.EqualTo("before assistant"));
            Assert.That(history.SelectMany(message => message.Items).OfType<TextContent>().Any(item => item.Text == string.Empty), Is.False);
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
    public void BuildSelectedHistory_WhenCanceled_DoesNotTurnCancellationIntoAnAttachmentOmission()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var user = new UserChatMessage("message", [new FileAttachment(new DirectLocaleKey("file"), "missing.png", "missing", "image/png")]);
        Assert.ThrowsAsync<OperationCanceledException>(async () => await ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
            Substitute.For<IPromptRenderer>(), "system", [user], Modalities.Image, cancellation.Token));
    }
}