using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI.Tests;

[Category("LlmIntegration")]
public sealed class SdkStreamLifecycleTests
{
    public static IEnumerable<Provider> Providers() => Enum.GetValues<Provider>();

    [TestCaseSource(nameof(Providers))]
    public async Task Stream_WhenHttpEndsWithoutTerminalEvent_PreservesPartialTextWithoutInventingFinishReason(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = 200, body = FirstEvents(provider), headers = new Dictionary<string, string[]> { ["Content-Type"] = [ContentType(provider)] }
        });
        using var client = server.CreateClient();
        var updates = await SdkStreamingTests.CollectAsync(provider.CreateService(client));
        Assert.That(string.Concat(updates.Select(update => update.Content)), Is.EqualTo("partial"));
        Assert.That(updates.Select(update => update.Metadata?.GetValueOrDefault("FinishReason")?.ToString()).Where(reason => !string.IsNullOrEmpty(reason)), Is.Empty);
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCaseSource(nameof(Providers))]
    public async Task Stream_WhenHttpBodyBreaksAfterText_PreservesProgressAndFailsWithoutSdkReplay(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await RespondWithOpenBodyAsync(server, provider, shouldDropConnection: true);
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        var text = new StringBuilder();
        var exception = await SdkStreamingTests.CatchAsync(async () =>
        {
            await foreach (var update in provider.CreateService(client).GetStreamingChatMessageContentsAsync(History())) text.Append(update.Content);
        });
        Assert.That(text.ToString(), Is.EqualTo("partial"));
        Assert.That(exception, Is.Not.InstanceOf<OperationCanceledException>());
        await gate.ResponseReleased.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCaseSource(nameof(Providers))]
    public async Task Stream_WhenDisposedAfterText_ReleasesResponseWithoutServerTeardown(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await RespondWithOpenBodyAsync(server, provider, shouldDropConnection: false);
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        using var cancellation = new CancellationTokenSource();
        var enumerator = provider.CreateService(client).GetStreamingChatMessageContentsAsync(History(), cancellationToken: cancellation.Token).GetAsyncEnumerator();
        HttpResponseMessage? response = null;
        try
        {
            await ReadPartialTextAsync(enumerator);
            response = await gate.ResponseReceived.WaitAsync(TimeSpan.FromSeconds(3));
            await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await gate.ResponseReleased.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(cancellation.IsCancellationRequested, Is.False);
            Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
        }
        finally
        {
            cancellation.Cancel();
            response?.Dispose();
            await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [TestCaseSource(nameof(Providers))]
    public async Task Stream_WhenReadIsPending_CancelsAndDisposesResponse(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await RespondWithOpenBodyAsync(server, provider, shouldDropConnection: false);
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        using var cancellation = new CancellationTokenSource();
        var enumerator = provider.CreateService(client).GetStreamingChatMessageContentsAsync(History(), cancellationToken: cancellation.Token).GetAsyncEnumerator();
        HttpResponseMessage? response = null;
        Task<bool>? pending = null;
        try
        {
            await ReadPartialTextAsync(enumerator);
            response = await gate.ResponseReceived.WaitAsync(TimeSpan.FromSeconds(3));
            pending = enumerator.MoveNextAsync().AsTask();
            Assert.That(pending.IsCompleted, Is.False, "Cancel only after consuming the prefix and starting the next read.");
            cancellation.Cancel();
            var exception = await SdkStreamingTests.CatchAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
            await gate.ResponseReleased.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            cancellation.Cancel();
            response?.Dispose();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException) { }
            }
            await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task ReadPartialTextAsync(IAsyncEnumerator<StreamingChatMessageContent> enumerator)
    {
        for (var index = 0; index < 10; index++)
        {
            Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            if (enumerator.Current.Content is not { Length: > 0 } text) continue;
            Assert.That(text, Is.EqualTo("partial"));
            return;
        }
        Assert.Fail("The SDK did not emit the scripted text prefix.");
    }

    private static ChatHistory History() => new([new ChatMessageContent(AuthorRole.User, "hello")]);

    private static Task RespondWithOpenBodyAsync(MockServerSession server, Provider provider, bool shouldDropConnection)
    {
        // A valid prefix is one HTTP chunk; deliberately omit the zero-length terminal
        // chunk. The server either closes the socket or holds it until the client closes.
        var prefix = FirstEvents(provider);
        var wire = "HTTP/1.1 200 OK\r\nContent-Type: " + ContentType(provider) + "\r\nTransfer-Encoding: chunked\r\n\r\n" +
            Encoding.UTF8.GetByteCount(prefix).ToString("X") + "\r\n" + prefix + "\r\n";
        return server.FailTransportAsync(new { responseBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(wire)), dropConnection = shouldDropConnection });
    }

    private static string ContentType(Provider provider) => provider == Provider.Ollama ? "application/x-ndjson" : "text/event-stream";

    // Independently written synthetic prefixes, deliberately without terminal evidence.
    // They establish parser state before socket faults; no fixture asserts API policy.
    private static string FirstEvents(Provider provider) => provider switch
    {
        Provider.OpenAI or Provider.Mistral => "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial\"},\"finish_reason\":null}]}\n\n",
        Provider.OpenAIResponses => "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"model\":\"test-model\",\"output\":[]}}\n\nevent: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg-test\",\"output_index\":0,\"content_index\":0,\"delta\":\"partial\"}\n\n",
        Provider.Anthropic => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-test\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"test-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":12,\"output_tokens\":0}}}\n\nevent: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n",
        Provider.Gemini => "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"partial\"}]}}],\"modelVersion\":\"test-model\"}\n\n",
        Provider.Ollama => "{\"model\":\"test-model\",\"message\":{\"role\":\"assistant\",\"content\":\"partial\"},\"done\":false}\n",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
