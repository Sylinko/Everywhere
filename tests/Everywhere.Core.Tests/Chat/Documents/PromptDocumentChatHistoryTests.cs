using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Documents;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat.Documents;

public sealed class PromptDocumentChatHistoryTests
{
    [Test]
    public async Task HistoryRendersPromptDocumentWithoutFlatteningPersistedResult()
    {
        const string required = "required result ";
        var optional = string.Join(' ', Enumerable.Repeat("optional", 80));
        PromptDocument document =
        [
            new PromptTokenLimit(
                TokenHelper.EstimateTokenCount(required),
                new PromptText(required).WithPriority(10),
                new PromptText(optional).WithPriority(0))
        ];
        var call = new FunctionCallContent("read_file", "file_system", "call-1", null);
        var persistedResult = new FunctionResultContent(call, document);
        var bytes = MessagePackSerializer.Serialize(persistedResult);
        var result = MessagePackSerializer.Deserialize<FunctionResultContent>(bytes);
        var functionMessage = new FunctionCallChatMessage(LucideIconKind.File, null);
        functionMessage.Calls.Add(call);
        functionMessage.Results.Add(result);
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(functionMessage));

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system",
            [assistant],
            -1,
            Modalities.Text);

        var modelResult = history
            .Single(static message => message.Role == AuthorRole.Tool)
            .Items
            .OfType<FunctionResultContent>()
            .Single();
        var restoredDocument = result.Result as PromptDocument;

        Assert.Multiple(() =>
        {
            Assert.That(modelResult.Result, Is.EqualTo(required));
            Assert.That(restoredDocument, Is.Not.Null);
            Assert.That(restoredDocument!.ToString(), Is.EqualTo(required));
            Assert.That(persistedResult.Result, Is.SameAs(document));
        });
    }

    [Test]
    public async Task HistoryRendersStandalonePromptNodeWithoutFlatteningPersistedResult()
    {
        const string required = "required result ";
        var optional = string.Join(' ', Enumerable.Repeat("optional", 80));
        PromptNode node = new PromptTokenLimit(
            TokenHelper.EstimateTokenCount(required),
            new PromptText(required).WithPriority(10),
            new PromptText(optional).WithPriority(0));
        var call = new FunctionCallContent("read_file", "file_system", "call-1", null);
        var persistedResult = new FunctionResultContent(call, node);
        var bytes = MessagePackSerializer.Serialize(persistedResult);
        var result = MessagePackSerializer.Deserialize<FunctionResultContent>(bytes);
        var functionMessage = new FunctionCallChatMessage(LucideIconKind.File, null);
        functionMessage.Calls.Add(call);
        functionMessage.Results.Add(result);
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(functionMessage));

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system",
            [assistant],
            -1,
            Modalities.Text);

        var modelResult = history
            .Single(static message => message.Role == AuthorRole.Tool)
            .Items
            .OfType<FunctionResultContent>()
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(modelResult.Result, Is.EqualTo(required));
            Assert.That(result.Result, Is.TypeOf<PromptTokenLimit>());
            Assert.That(persistedResult.Result, Is.SameAs(node));
        });
    }
}
