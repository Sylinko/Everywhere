using System.Net;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp.Models.Exceptions;

namespace Everywhere.AI.Tests;

[Category("LlmIntegration")]
public sealed class SdkStreamingTests
{
    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task Stream_WhenCompleted_PreservesTextAndSendsStreamingRequest(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.CompleteAsync(provider, new { text = "hello world", streaming = true, usage = new { inputTokens = 12, outputTokens = 8 } });
        using var client = server.CreateClient();
        var updates = await CollectAsync(provider.CreateService(client));
        Assert.That(string.Concat(updates.Select(update => update.Content)), Is.EqualTo("hello world"));
        var requests = await server.RequestsAsync();
        Assert.That(requests, Has.Length.EqualTo(1));
        var wireRequest = requests[0].GetRawText();
        Assert.That(wireRequest, Does.Contain(provider == Provider.Gemini ? "streamGenerateContent" : "stream"));
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task Stream_WhenToolCalled_PreservesFunctionAndArguments(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        if (provider == Provider.OpenAIResponses)
        {
            // MockServer 8.0.0's Responses codec omits the required call_id. Keep a
            // valid wire fixture as the normal-path control; separate tests cover missing IDs.
            var body = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "responses-tool.sse"));
            await server.RespondAsync(new { statusCode = 200, headers = new Dictionary<string, string[]> { ["Content-Type"] = ["text/event-stream"] }, body });
        }
        else await server.CompleteAsync(provider, new
        {
            streaming = true, stopReason = "tool_calls",
            toolCalls = new[] { new { id = "call-test", name = "read_file", arguments = "{\"path\":\"task.py\"}" } }
        });
        using var client = server.CreateClient();
        var updates = await CollectAsync(provider.CreateService(client));
        var calls = updates.SelectMany(update => update.Items).OfType<StreamingFunctionCallUpdateContent>().ToArray();
        Assert.That(string.Concat(calls.Select(call => call.Name)), Is.EqualTo("read_file"));
        Assert.That(string.Concat(calls.Select(call => call.Arguments)), Does.Contain("task.py"));
    }

    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    public async Task Stream_WhenConnectionDrops_PreservesTransportFailure(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.FailTransportAsync(new { dropConnection = true });
        using var client = server.CreateClient();
        var exception = await CatchAsync(() => CollectAsync(provider.CreateService(client)));
        Assert.That(exception, Is.InstanceOf<HttpRequestException>());
        Assert.That(((HttpRequestException)exception).StatusCode, Is.Null);
    }

    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task Stream_WhenCanceledDuringErrorBody_ThrowsCancellation(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.StreamAsync(new
        {
            statusCode = 400, events = new[] { new { data = "partial error", delay = new { timeUnit = "SECONDS", value = 10 } } }
        });
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        using var cancellation = new CancellationTokenSource();
        var pending = CatchAsync(() => CollectAsync(provider.CreateService(client), cancellation.Token));
        using var response = await gate.ResponseReceived.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancellation.Cancel();
            var exception = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
            await gate.ContentDisposed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            response.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public async Task Stream_WhenOllamaReturnsErrorObject_ThrowsProtocolError()
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new { statusCode = 200, headers = new { }, body = "{\"error\":\"model failed to load\"}\n" });
        using var client = server.CreateClient();
        var exception = await CatchAsync(() => CollectAsync(Provider.Ollama.CreateService(client)));
        Assert.That(exception, Is.TypeOf<ResponseError>());
        Assert.That(exception.Message, Does.Contain("model failed to load"));
    }

    [Test]
    public async Task Stream_WhenOllamaLineReadIsPending_CancelsAndDisposesRequest()
    {
        await using var server = await MockServerSession.ConnectAsync();
        // Raw HTTP with an unfinished chunk keeps a real socket read pending; the action
        // deliberately leaves the connection open until client cancellation closes it.
        var headers = "HTTP/1.1 200 OK\r\nContent-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\n\r\n";
        var line = "{\"model\":\"test-model\",\"message\":{\"role\":\"assistant\",\"content\":\"hello\"},\"done\":false}\n";
        await server.FailTransportAsync(new { responseBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(headers + Encoding.UTF8.GetByteCount(line).ToString("X") + "\r\n" + line + "\r\n")), dropConnection = false });
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        using var cancellation = new CancellationTokenSource();
        var history = new ChatHistory();
        history.AddUserMessage("hello");
        var enumerator = Provider.Ollama.CreateService(client).GetStreamingChatMessageContentsAsync(history, cancellationToken: cancellation.Token).GetAsyncEnumerator();
        var first = enumerator.MoveNextAsync().AsTask();
        using var response = await gate.ResponseReceived.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = first;
        try
        {
            Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            pending = enumerator.MoveNextAsync().AsTask();
            Assert.That(pending.IsCompleted, Is.False, "Cancellation must occur while a socket read is pending.");
            cancellation.Cancel();
            var exception = await CatchAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
            await gate.ContentDisposed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            response.Dispose();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException) { }
            await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    public static async Task<List<StreamingChatMessageContent>> CollectAsync(IChatCompletionService service, CancellationToken cancellationToken = default)
    {
        var history = new ChatHistory();
        history.AddUserMessage("hello");
        var updates = new List<StreamingChatMessageContent>();
        await foreach (var update in service.GetStreamingChatMessageContentsAsync(history, cancellationToken: cancellationToken)) updates.Add(update);
        return updates;
    }

    public static async Task<Exception> CatchAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { return exception; }
        Assert.Fail("The request unexpectedly succeeded.");
        throw new InvalidOperationException("Unreachable");
    }

}
