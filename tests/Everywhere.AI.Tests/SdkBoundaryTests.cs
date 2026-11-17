using System.Text;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Everywhere.AI.Tests;

[Category("LlmIntegration")]
public sealed class SdkBoundaryTests
{
    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    [TestCase(Provider.Ollama)]
    public async Task Stream_WhenCompleted_PreservesFinishReasonAlongsideUsage(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        if (provider == Provider.OpenAI)
            await RespondAsync(server, await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "openai-text-usage.sse")));
        else if (provider == Provider.Ollama)
            await RespondAsync(server, "{\"model\":\"test-model\",\"message\":{\"role\":\"assistant\",\"content\":\"hello\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":12,\"eval_count\":8}\n");
        else
            await server.CompleteAsync(provider, new { text = "hello", streaming = true, usage = new { inputTokens = 12, outputTokens = 8 } });
        using var client = server.CreateClient();
        var updates = await SdkStreamingTests.CollectAsync(provider.CreateClient(client));
        var usage = updates.SelectMany(static update => update.Contents).OfType<UsageContent>().Last();
        Assert.That(updates.Select(static update => update.FinishReason?.Value), Does.Contain("stop"));
        Assert.That(usage.Details.InputTokenCount, Is.EqualTo(12));
        Assert.That(usage.Details.OutputTokenCount, Is.EqualTo(8));
        Assert.That(string.Concat(updates.Select(static update => update.Text)), Is.EqualTo("hello"));
    }

    [TestCase(Provider.OpenAI, "length")]
    [TestCase(Provider.Anthropic, "max_tokens")]
    [TestCase(Provider.Mistral, "length")]
    [TestCase(Provider.Gemini, "length")]
    [TestCase(Provider.GeminiInteractions, "length")]
    public async Task Stream_WhenOutputTruncated_PreservesProviderTerminationEvidence(Provider provider, string stopReason)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.CompleteAsync(provider, new { text = "partial", streaming = true, stopReason });
        using var client = server.CreateClient();
        var updates = await SdkStreamingTests.CollectAsync(provider.CreateClient(client));
        var reasons = updates.Select(static update => update.FinishReason?.Value).Where(static reason => reason is not null).ToArray();
        Assert.That(reasons, Does.Contain(provider == Provider.GeminiInteractions ? "incomplete" : "length"));
        Assert.That(string.Concat(updates.Select(static update => update.Text)), Is.EqualTo("partial"));
    }

    [TestCase("max_output_tokens", "length")]
    [TestCase("content_filter", "content_filter")]
    public async Task Stream_WhenResponsesIncomplete_PreservesReasonAndUsage(string reason, string expectedReason)
    {
        await using var server = await MockServerSession.ConnectAsync();
        var transcript = "event: response.incomplete\ndata: {\"type\":\"response.incomplete\",\"sequence_number\":1,\"response\":{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":1,\"status\":\"incomplete\",\"model\":\"test-model\",\"output\":[],\"incomplete_details\":{\"reason\":\"" + reason + "\"},\"usage\":{\"input_tokens\":12,\"output_tokens\":8,\"total_tokens\":20}}}\n\n";
        await RespondAsync(server, transcript);
        using var client = server.CreateClient();
        var updates = await SdkStreamingTests.CollectAsync(Provider.OpenAIResponses.CreateClient(client));
        Assert.That(updates.Select(static update => update.FinishReason?.Value), Does.Contain(expectedReason));
        var usage = updates.SelectMany(static update => update.Contents).OfType<UsageContent>().Single();
        Assert.That(usage.Details.OutputTokenCount, Is.EqualTo(8));
    }

    [Test]
    public async Task Stream_WhenResponsesFailed_PreservesErrorContent()
    {
        await using var server = await MockServerSession.ConnectAsync();
        await RespondAsync(server, "event: response.failed\ndata: {\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{\"id\":\"resp-test\",\"object\":\"response\",\"created_at\":1,\"status\":\"failed\",\"model\":\"test-model\",\"output\":[],\"error\":{\"code\":\"server_error\",\"message\":\"upstream failed\"}}}\n\n");
        using var client = server.CreateClient();
        using var chatClient = ProviderExtensions.CreateResponsesClient(client);
        var errors = new List<ErrorContent>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            errors.AddRange(update.Contents.OfType<ErrorContent>());
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Message, Is.EqualTo("upstream failed"));
        Assert.That(errors[0].ErrorCode, Is.EqualTo("server_error"));
    }

    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    [TestCase(Provider.Anthropic)]
    public async Task Stream_WhenHttpFailure_PreservesSelectedHeadersAndDisposesResponse(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = 429,
            body = "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}",
            headers = new Dictionary<string, string[]>
            {
                ["Retry-After"] = ["2"], ["Retry-After-Ms"] = ["250"], ["request-id"] = ["request-first"],
                ["x-request-id"] = ["request-second"], ["x-unrelated"] = ["do not retain"]
            }
        });
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        var exception = await SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateClient(client)));
        await gate.ContentDisposed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(exception.Data["Everywhere.Http.Retry-After"], Is.EqualTo("2"));
        Assert.That(exception.Data["Everywhere.Http.Retry-After-Ms"], Is.EqualTo("250"));
        Assert.That(exception.Data["Everywhere.Http.request-id"], Is.EqualTo("request-first"));
        Assert.That(exception.Data["Everywhere.Http.x-request-id"], Is.EqualTo("request-second"));
        Assert.That(exception.Data.Contains("Everywhere.Http.x-unrelated"), Is.False);
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
    }

    [TestCase(Provider.Gemini)]
    [TestCase(Provider.GeminiInteractions)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    public async Task Stream_WhenErrorBodyIsInterrupted_DisposesResponseAndPreservesReadFailure(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.FailTransportAsync(new
        {
            responseBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 10000\r\nConnection: close\r\n\r\npartial")),
            dropConnection = true
        });
        var gate = new ResponseGateHandler();
        using var client = server.CreateClient(gate);
        var exception = await SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateClient(client)));
        await gate.ContentDisposed.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(exception, Is.InstanceOf<HttpRequestException>().Or.InstanceOf<IOException>());
    }

    private static Task RespondAsync(MockServerSession server, string body) => server.RespondAsync(new
    {
        statusCode = 200,
        headers = new Dictionary<string, string[]> { ["Content-Type"] = ["text/event-stream; charset=utf-8"] },
        body
    });
}
