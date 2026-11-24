using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Harness;
using Everywhere.Prompting;
using Everywhere.Prompting.Documents;
using Tabler.Avalonia;
using MessagePack;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace Everywhere.Core.Tests.Prompting.Documents;

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
        var call = new ToolCallRecord("read_file", "file_system", "call-1", null);
        var persistedResult = new ToolResultRecord(call.FunctionName, call.PluginName, call.Id, document);
        var bytes = MessagePackSerializer.Serialize(persistedResult);
        var result = MessagePackSerializer.Deserialize<ToolResultRecord>(bytes);
        var functionMessage = new FunctionCallChatMessage(TablerIconKind.File, null);
        functionMessage.AddCall(call);
        functionMessage.AddResult(result);
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(functionMessage));

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system",
            [assistant],
            -1,
            Modalities.Text);

        var modelResult = history
            .Single(static message => message.Role == ChatRole.Tool)
            .Contents
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
        var call = new ToolCallRecord("read_file", "file_system", "call-1", null);
        var persistedResult = new ToolResultRecord(call.FunctionName, call.PluginName, call.Id, node);
        var bytes = MessagePackSerializer.Serialize(persistedResult);
        var result = MessagePackSerializer.Deserialize<ToolResultRecord>(bytes);
        var functionMessage = new FunctionCallChatMessage(TablerIconKind.File, null);
        functionMessage.AddCall(call);
        functionMessage.AddResult(result);
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(functionMessage));

        var history = await ChatHistoryBuilder.BuildChatHistoryAsync(
            Substitute.For<IPromptRenderer>(),
            "system",
            [assistant],
            -1,
            Modalities.Text);

        var modelResult = history
            .Single(static message => message.Role == ChatRole.Tool)
            .Contents
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
