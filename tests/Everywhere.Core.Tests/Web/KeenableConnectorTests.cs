using System.Net;
using System.Text;
using System.Text.Json;
using Everywhere.Web;

namespace Everywhere.Core.Tests.Web;

public sealed class KeenableConnectorTests
{
    private static readonly Uri EndPoint = new("https://api.keenable.ai/v1/search");

    private const string ResultsJson = """
        {
          "query": "what is keenable?",
          "mode": "pro",
          "results": [
            { "title": "Keenable", "url": "https://keenable.ai", "description": "", "snippet": "Search API for AI agents" },
            { "title": "No url", "snippet": "dropped" },
            { "title": "Docs", "url": "https://docs.keenable.ai", "description": "Keenable docs" }
          ]
        }
        """;

    [Test]
    public async Task SearchAsync_WithoutApiKey_PostsToPublicPathAndMapsFields()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var connector = CreateConnector(null, request =>
        {
            captured = request;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK, ResultsJson);
        });

        var results = (await connector.SearchAsync("what is keenable?", 5)).ToList();

        using var json = JsonDocument.Parse(body ?? "{}");
        Assert.Multiple(() =>
        {
            Assert.That(captured?.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(captured?.RequestUri?.ToString(), Is.EqualTo("https://api.keenable.ai/v1/search/public"));
            Assert.That(captured?.Headers.GetValues("X-Keenable-Title").Single(), Is.EqualTo("Everywhere"));
            Assert.That(captured?.Headers.Contains("X-API-Key"), Is.False);
            Assert.That(json.RootElement.GetProperty("query").GetString(), Is.EqualTo("what is keenable?"));
            Assert.That(json.RootElement.GetProperty("max_results").GetInt32(), Is.EqualTo(5));
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].Name, Is.EqualTo("Keenable"));
            Assert.That(results[0].Link, Is.EqualTo("https://keenable.ai"));
            Assert.That(results[0].Value, Is.EqualTo("Search API for AI agents"));
            Assert.That(results[1].Value, Is.EqualTo("Keenable docs"));
        });
    }

    [Test]
    public async Task SearchAsync_WithApiKey_PostsToKeyedPathWithHeader()
    {
        HttpRequestMessage? captured = null;
        using var connector = CreateConnector("test-key", request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, """{ "results": [] }""");
        });

        var results = await connector.SearchAsync("query", 3);

        Assert.Multiple(() =>
        {
            Assert.That(captured?.RequestUri?.ToString(), Is.EqualTo("https://api.keenable.ai/v1/search"));
            Assert.That(captured?.Headers.GetValues("X-API-Key").Single(), Is.EqualTo("test-key"));
            Assert.That(captured?.Headers.GetValues("X-Keenable-Title").Single(), Is.EqualTo("Everywhere"));
            Assert.That(results, Is.Empty);
        });
    }

    [Test]
    public async Task SearchAsync_WhenCountExceedsCap_ClampsMaxResultsToFifty()
    {
        string? body = null;
        using var connector = CreateConnector(null, request =>
        {
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK, """{ "results": [] }""");
        });

        await connector.SearchAsync("query", 100);

        using var json = JsonDocument.Parse(body ?? "{}");
        Assert.That(json.RootElement.GetProperty("max_results").GetInt32(), Is.EqualTo(50));
    }

    [Test]
    public async Task SearchAsync_WhenRateLimited_ThrowsWithStatusAndBody()
    {
        using var connector = CreateConnector(
            "test-key",
            _ => JsonResponse(HttpStatusCode.TooManyRequests, """{ "code": "public_rate_limit_hourly" }"""));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => connector.SearchAsync("query", 3));

        Assert.Multiple(() =>
        {
            Assert.That(exception?.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(exception?.Message, Does.Contain("public_rate_limit_hourly"));
            Assert.That(exception?.Message, Does.Not.Contain("test-key"));
        });
    }

    private static KeenableConnector CreateConnector(string? apiKey, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(apiKey, new HttpClient(new DelegateHandler(handler)), EndPoint);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
