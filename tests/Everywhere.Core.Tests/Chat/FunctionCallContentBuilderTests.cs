using Everywhere.Chat;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google.Core;

namespace Everywhere.Core.Tests.Chat;

public class FunctionCallContentBuilderTests
{
    [Test]
    public void Append_ReturnsTrue_ForArgumentOnlyUpdate()
    {
        var builder = new ChatService.FunctionCallContentBuilder();

        var result = builder.Append(CreateStreamingContent(
            new StreamingFunctionCallUpdateContent(arguments: "{\"path\":")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(builder.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void Build_JoinsCallIdNameAndArgumentsAcrossStreamingChunks()
    {
        var builder = new ChatService.FunctionCallContentBuilder();

        builder.Append(CreateStreamingContent(
            new StreamingFunctionCallUpdateContent(callId: "call-1", functionCallIndex: 2)));
        builder.Append(CreateStreamingContent(
            new StreamingFunctionCallUpdateContent(name: "FileSystem-ReadFile", functionCallIndex: 2)));
        builder.Append(CreateStreamingContent(
            new StreamingFunctionCallUpdateContent(arguments: "{\"path\":", functionCallIndex: 2)));
        builder.Append(CreateStreamingContent(
            new StreamingFunctionCallUpdateContent(arguments: "\"README.md\"}", functionCallIndex: 2)));

        var functionCalls = builder.Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(functionCalls, Has.Count.EqualTo(1));
            Assert.That(functionCalls[0].Id, Is.EqualTo("call-1"));
            Assert.That(functionCalls[0].FunctionName, Is.EqualTo("FileSystem-ReadFile"));
            Assert.That(functionCalls[0].Arguments?["path"]?.ToString(), Is.EqualTo("README.md"));
            Assert.That(functionCalls[0].Exception, Is.Null);
        }
    }

    [Test]
    public void Build_PreservesFullFunctionCallsWithDefaultFunctionCallIndex()
    {
        var builder = new ChatService.FunctionCallContentBuilder();

        builder.Append(CreateStreamingContent(
            new StreamingFunctionCallUpdateContent(
                callId: "call-1",
                name: "FileSystem-ReadFile",
                arguments: "{\"path\":\"README.md\"}"),
            new StreamingFunctionCallUpdateContent(
                callId: "call-2",
                name: "Terminal-Run",
                arguments: "{\"command\":\"dotnet test\"}")));

        var functionCalls = builder.Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(functionCalls, Has.Count.EqualTo(2));
            Assert.That(functionCalls[0].Id, Is.EqualTo("call-1"));
            Assert.That(functionCalls[0].FunctionName, Is.EqualTo("FileSystem-ReadFile"));
            Assert.That(functionCalls[0].Arguments?["path"]?.ToString(), Is.EqualTo("README.md"));
            Assert.That(functionCalls[1].Id, Is.EqualTo("call-2"));
            Assert.That(functionCalls[1].FunctionName, Is.EqualTo("Terminal-Run"));
            Assert.That(functionCalls[1].Arguments?["command"]?.ToString(), Is.EqualTo("dotnet test"));
        }
    }

    [Test]
    public void Append_ReturnsFalse_WhenNoFunctionCallUpdateExists()
    {
        var builder = new ChatService.FunctionCallContentBuilder();

        var result = builder.Append(new StreamingChatMessageContent(AuthorRole.Assistant, "hello"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(builder.Count, Is.Zero);
        }
    }

    [Test]
    public void Append_ReturnsTrue_ForGeminiRawResponseWithFunctionCall()
    {
        var builder = new ChatService.FunctionCallContentBuilder();
        var rawResponse = new GeminiResponse
        {
            Candidates =
            [
                new GeminiResponseCandidate
                {
                    Content = new GeminiContent
                    {
                        Parts =
                        [
                            new GeminiPart
                            {
                                FunctionCall = new GeminiPart.FunctionCallPart
                                {
                                    FunctionName = "FileSystem-ReadFile"
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var result = builder.Append(new StreamingChatMessageContent(
            AuthorRole.Assistant,
            content: null,
            innerContent: rawResponse));

        Assert.That(result, Is.True);
    }

    private static StreamingChatMessageContent CreateStreamingContent(params StreamingFunctionCallUpdateContent[] updates)
    {
        var content = new StreamingChatMessageContent(AuthorRole.Assistant, null);

        foreach (var update in updates)
        {
            content.Items.Add(update);
        }

        return content;
    }
}
