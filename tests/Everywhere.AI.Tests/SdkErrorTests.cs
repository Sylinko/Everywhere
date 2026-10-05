using System.ClientModel;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.SemanticKernel;

namespace Everywhere.AI.Tests;

[Category("LlmIntegration")]
public sealed class SdkErrorTests
{
    public static IEnumerable<TestCaseData> ErrorCases()
    {
        var directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Errors");
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var fixtures = JsonSerializer.Deserialize(File.ReadAllText(file), FixtureJsonContext.Default.ErrorFixtureArray)
                ?? throw new InvalidDataException("An error fixture file must contain an array.");
            foreach (var fixture in fixtures)
            {
                if (fixture.Provenance is not ("source" or "synthetic" or "observed")) throw new InvalidDataException($"Missing provenance for {fixture.Id}.");
                if (fixture.Provenance == "source" && fixture.Source is null) throw new InvalidDataException($"Missing source for {fixture.Id}.");
                if (fixture.Provenance == "observed" && (fixture.ObservedAt is null || fixture.Notes is null))
                    throw new InvalidDataException($"Observed case {fixture.Id} requires a date and sanitized capture notes.");
                foreach (var provider in fixture.Providers)
                    yield return new TestCaseData(provider, fixture).SetName($"Error_{fixture.Id}_{provider}");
            }
        }
    }

    [TestCaseSource(nameof(ErrorCases))]
    public async Task Stream_WhenServerRejects_PreservesStatusAndBodyWithoutRetry(Provider provider, ErrorFixture fixture)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = fixture.Status, body = new { type = "STRING", @string = fixture.WireBody, charset = "UTF-8" },
            headers = new Dictionary<string, string[]> { ["Content-Type"] = [(fixture.ContentType ?? "application/json") + "; charset=utf-8"], ["Retry-After"] = ["1"], ["x-request-id"] = [fixture.Id], ["request-id"] = [fixture.Id] }
        });
        using var client = server.CreateClient();
        var exception = await SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateService(client)));
        var evidence = ExtractEvidence(exception);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(evidence.Status, Is.EqualTo(fixture.Status));
            Assert.That(evidence.Body, Is.EqualTo(fixture.WireBody));
            Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(1));
        }

        if (exception is ClientResultException openAI && openAI.GetRawResponse() is { } rawResponse)
        {
            Assert.That(rawResponse.Headers.TryGetValue("Retry-After", out var delay), Is.True);
            Assert.That(delay, Is.EqualTo("1"));
            Assert.That(rawResponse.Headers.TryGetValue("x-request-id", out var requestId), Is.True);
            Assert.That(requestId, Is.EqualTo(fixture.Id));
        }
        else if (provider is Provider.Gemini or Provider.Mistral or Provider.Ollama)
        {
            Assert.That(exception.Data["Everywhere.Http.Retry-After"], Is.EqualTo("1"));
            Assert.That(exception.Data["Everywhere.Http.x-request-id"], Is.EqualTo(fixture.Id));
        }
        // Anthropic retains selected headers through its static donor; the production
        // normalization tests also verify them at the common evidence entry point.
    }

    [TestCase(Provider.OpenAI)]
    [TestCase(Provider.OpenAIResponses)]
    [TestCase(Provider.Anthropic)]
    public async Task Stream_WhenSdkRetryBudgetIsOne_PerformsExactlyTwoSends(Provider provider)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = 503, body = "{\"error\":{\"type\":\"api_error\",\"message\":\"temporarily unavailable\"}}",
            headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"], ["Retry-After-Ms"] = ["1"] }
        });
        using var client = server.CreateClient();
        var exception = await SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateService(client, maxRetries: 1)));
        Assert.That(ExtractEvidence(exception).Status, Is.EqualTo(503));
        Assert.That(await server.RequestsAsync(), Has.Length.EqualTo(2));
    }

    [TestCase(Provider.OpenAI, 429)]
    [TestCase(Provider.OpenAIResponses, 429)]
    [TestCase(Provider.Anthropic, 429)]
    [TestCase(Provider.OpenAI, 503)]
    [TestCase(Provider.OpenAIResponses, 503)]
    [TestCase(Provider.Anthropic, 503)]
    public async Task Stream_WhenSdkRetriesThenSucceeds_PreservesRequestBodyAndReturnsOnlySuccess(Provider provider, int status)
    {
        await using var server = await MockServerSession.ConnectAsync();
        await server.RespondAsync(new
        {
            statusCode = status, body = "{\"error\":{\"type\":\"api_error\",\"message\":\"try again\"}}",
            headers = new Dictionary<string, string[]> { ["Retry-After-Ms"] = ["1"] }
        }, times: 2);
        await server.CompleteAsync(provider, new { text = "recovered", streaming = true }, times: 1);
        using var client = server.CreateClient();
        var updates = await SdkStreamingTests.CollectAsync(provider.CreateService(client, maxRetries: 2));
        Assert.That(string.Concat(updates.Select(update => update.Content)), Is.EqualTo("recovered"));
        var requests = await server.RequestsAsync();
        Assert.That(requests, Has.Length.EqualTo(3));
        Assert.That(requests.Select(request => request.GetProperty("body").GetRawText()), Is.All.EqualTo(requests[0].GetProperty("body").GetRawText()));
    }

    [TestCase(Provider.Gemini)]
    [TestCase(Provider.Mistral)]
    [TestCase(Provider.Ollama)]
    [TestCase(Provider.Anthropic)]
    public async Task Stream_WhenTwoRequestsFailConcurrently_KeepsResponseEvidenceSeparate(Provider provider)
    {
        await using var first = await MockServerSession.ConnectAsync();
        await using var second = await MockServerSession.ConnectAsync();
        await first.RespondAsync(new { statusCode = 429, body = "first failure", headers = new Dictionary<string, string[]> { ["Retry-After"] = ["3"], ["x-request-id"] = ["first"] } });
        await second.RespondAsync(new { statusCode = 503, body = "second failure", headers = new Dictionary<string, string[]> { ["Retry-After"] = ["7"], ["x-request-id"] = ["second"] } });
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        var exceptions = await Task.WhenAll(
            SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateService(firstClient))),
            SdkStreamingTests.CatchAsync(() => SdkStreamingTests.CollectAsync(provider.CreateService(secondClient))));
        Assert.That(ExtractEvidence(exceptions[0]), Is.EqualTo((429, "first failure")));
        Assert.That(ExtractEvidence(exceptions[1]), Is.EqualTo((503, "second failure")));
        Assert.That(exceptions[0].Data["Everywhere.Http.x-request-id"], Is.EqualTo("first"));
        Assert.That(exceptions[1].Data["Everywhere.Http.x-request-id"], Is.EqualTo("second"));
    }

    private static (int Status, string Body) ExtractEvidence(Exception exception) => exception switch
    {
        ClientResultException failure => (failure.Status, failure.GetRawResponse()?.Content.ToString() ?? string.Empty),
        AnthropicApiException failure => ((int)failure.StatusCode, failure.ResponseBody),
        HttpOperationException failure => ((int?)failure.StatusCode ?? 0, failure.ResponseContent ?? string.Empty),
        HttpRequestException failure => ((int?)failure.StatusCode ?? 0, failure.Data["Everywhere.Http.ResponseBody"] as string ?? string.Empty),
        _ when exception.Data["Everywhere.Http.StatusCode"] is int status => (status, exception.Data["Everywhere.Http.ResponseBody"] as string ?? string.Empty),
        _ => throw new AssertionException($"Unexpected SDK exception type: {exception}")
    };
}
