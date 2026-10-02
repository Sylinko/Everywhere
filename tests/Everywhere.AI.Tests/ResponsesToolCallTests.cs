using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Everywhere.AI.Tests;

[Category("LlmIntegration")]
public sealed class ResponsesToolCallTests
{
    [TestCase(false, null)]
    [TestCase(true, null)]
    [TestCase(true, "")]
    [TestCase(true, "call-test")]
    public async Task Stream_WhenCallIdIsMissingOrPresent_RoundTripsMatchingToolResult(bool hasCallId, string? callId)
    {
        await using var server = await MockServerSession.ConnectAsync();
        var transcript = await ReadTranscriptAsync(hasCallId, callId);
        await RespondWithTranscriptAsync(server, transcript);
        var calls = await RoundTripAsync(server, expectedCount: 1);
        if (!string.IsNullOrEmpty(callId)) Assert.That(calls[0].CallId, Is.EqualTo(callId));
        Assert.That(calls[0].Arguments?["path"]?.ToString(), Is.EqualTo("task.py"));
    }

    [Test]
    public async Task Stream_WhenParallelCallsLackIds_RoundTripsDistinctIdsForSameFunction()
    {
        await using var server = await MockServerSession.ConnectAsync();
        // MockServer's own Responses codec omits call_id, exercising compatibility
        // against an independent generator instead of a corrected transcript.
        await server.CompleteAsync(Provider.OpenAIResponses, new
        {
            streaming = true,
            toolCalls = new[]
            {
                new { id = "source-first", name = "read_file", arguments = "{\"path\":\"first.py\"}" },
                new { id = "source-second", name = "read_file", arguments = "{\"path\":\"second.py\"}" }
            }
        }, times: 1);
        var calls = await RoundTripAsync(server, expectedCount: 2);
        Assert.That(calls.Select(call => call.Name), Is.All.EqualTo("read_file"));
        Assert.That(calls.Select(call => call.Arguments?["path"]?.ToString()), Is.EqualTo(new[] { "first.py", "second.py" }));
    }

    [Test]
    public async Task Stream_WhenArgumentsAreInvalidAndIdMissing_PreservesParsingException()
    {
        await using var server = await MockServerSession.ConnectAsync();
        var transcript = await ReadTranscriptAsync(hasCallId: false, callId: null, arguments: "{invalid-json");
        await RespondWithTranscriptAsync(server, transcript);
        using var httpClient = server.CreateClient();
        using var chatClient = ProviderExtensions.CreateResponsesClient(httpClient);
        var calls = await ReadCallsAsync(chatClient, [new ChatMessage(ChatRole.User, "Read task.py")]);
        Assert.That(calls, Has.Length.EqualTo(1));
        Assert.That(calls[0].CallId, Is.Not.Empty);
        Assert.That(calls[0].Exception, Is.TypeOf<InvalidOperationException>());
        Assert.That(calls[0].Exception?.InnerException, Is.TypeOf<JsonException>());
        Assert.That(calls[0].Arguments, Is.Null);
    }

    private static async Task<FunctionCallContent[]> RoundTripAsync(MockServerSession server, int expectedCount)
    {
        await server.CompleteAsync(Provider.OpenAIResponses, new { text = "finished", streaming = true });
        using var httpClient = server.CreateClient();
        using var chatClient = ProviderExtensions.CreateResponsesClient(httpClient);
        var history = new List<ChatMessage> { new(ChatRole.User, "Read the requested files") };
        var calls = await ReadCallsAsync(chatClient, history);
        Assert.That(calls, Has.Length.EqualTo(expectedCount));
        Assert.That(calls.Select(call => call.CallId), Is.All.Not.Null.And.Not.Empty);
        Assert.That(calls.Select(call => call.CallId).Distinct().Count(), Is.EqualTo(expectedCount));

        history.Add(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList()));
        history.Add(new ChatMessage(ChatRole.Tool, calls.Select((call, index) => (AIContent)new FunctionResultContent(call.CallId, $"result-{index}")).ToList()));
        var text = string.Empty;
        await foreach (var update in chatClient.GetStreamingResponseAsync(history)) text += update.Text;
        Assert.That(text, Is.EqualTo("finished"));

        var requests = await server.RequestsAsync();
        Assert.That(requests, Has.Length.EqualTo(2));
        using var request = ReadRequestBody(requests[1]);
        var input = request.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var wireCalls = input.Where(item => item.GetProperty("type").GetString() == "function_call").ToArray();
        var wireResults = input.Where(item => item.GetProperty("type").GetString() == "function_call_output").ToArray();
        Assert.That(wireCalls, Has.Length.EqualTo(expectedCount));
        Assert.That(wireResults, Has.Length.EqualTo(expectedCount));
        for (var index = 0; index < expectedCount; index++)
        {
            Assert.That(wireCalls[index].GetProperty("call_id").GetString(), Is.EqualTo(calls[index].CallId));
            Assert.That(wireResults[index].GetProperty("call_id").GetString(), Is.EqualTo(calls[index].CallId));
            Assert.That(wireResults[index].GetProperty("output").GetString(), Is.EqualTo($"result-{index}"));
        }
        Assert.That(request.RootElement.TryGetProperty("previous_response_id", out _), Is.False);
        return calls;
    }

    private static async Task<FunctionCallContent[]> ReadCallsAsync(IChatClient client, IEnumerable<ChatMessage> history)
    {
        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(history)) calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        return calls.ToArray();
    }

    private static Task RespondWithTranscriptAsync(MockServerSession server, string transcript) => server.RespondAsync(new
    {
        statusCode = 200, headers = new Dictionary<string, string[]> { ["Content-Type"] = ["text/event-stream"] }, body = transcript
    }, times: 1);

    private static async Task<string> ReadTranscriptAsync(bool hasCallId, string? callId, string? arguments = null)
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "responses-tool.sse"));
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = JsonNode.Parse(lines[index][6..]) ?? throw new InvalidDataException("Missing SSE data.");
            if (data["item"] is JsonObject item) UpdateCall(item);
            if (data["response"]?["output"] is JsonArray output)
                foreach (var call in output.OfType<JsonObject>()) UpdateCall(call);
            lines[index] = "data: " + data.ToJsonString();
        }
        return string.Join('\n', lines) + "\n\n";

        void UpdateCall(JsonObject item)
        {
            if (item["type"]?.GetValue<string>() != "function_call") return;
            if (hasCallId) item["call_id"] = callId;
            else item.Remove("call_id");
            if (arguments is not null && item["status"]?.GetValue<string>() == "completed") item["arguments"] = arguments;
        }
    }

    private static JsonDocument ReadRequestBody(JsonElement request)
    {
        var body = request.GetProperty("body");
        var json = body.ValueKind == JsonValueKind.String ? body.GetString() :
            body.TryGetProperty("json", out var jsonBody) ? jsonBody.GetRawText() : body.GetProperty("string").GetString();
        return JsonDocument.Parse(json ?? throw new InvalidDataException("Missing recorded request body."));
    }
}
