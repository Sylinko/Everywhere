using System.Net;
using System.Text;
using Everywhere.Web;

namespace Everywhere.Core.Tests.Web;

public sealed class SerplyConnectorTests
{
    private static readonly Uri EndPoint = new("https://api.serply.io/v1/search");

    [Test]
    public async Task SearchAsync_WithResults_SendsKeyedGetAndMapsFields()
    {
        HttpRequestMessage? captured = null;
        const string json = """
            {
              "results": [
                { "title": "Serply", "link": "https://serply.io", "description": "Google search API", "position": 1 },
                { "title": "No link", "description": "dropped" },
                { "title": "Docs", "link": "https://serply.io/docs" }
              ]
            }
            """;
        using var connector = CreateConnector(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, json);
        });

        var results = (await connector.SearchAsync("what is serply?", 5)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(captured?.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(captured?.RequestUri?.GetLeftPart(UriPartial.Path), Is.EqualTo("https://api.serply.io/v1/search"));
            Assert.That(captured?.RequestUri?.Query, Is.EqualTo("?q=what%20is%20serply%3F&num=5"));
            Assert.That(captured?.Headers.GetValues("X-Api-Key").Single(), Is.EqualTo("test-key"));
            Assert.That(captured?.Content, Is.Null);
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].Name, Is.EqualTo("Serply"));
            Assert.That(results[0].Link, Is.EqualTo("https://serply.io"));
            Assert.That(results[0].Value, Is.EqualTo("Google search API"));
            Assert.That(results[1].Value, Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public async Task SearchAsync_WhenCountExceedsCap_ClampsNumToTen()
    {
        HttpRequestMessage? captured = null;
        using var connector = CreateConnector(request =>
        {
            captured = request;
            return JsonResponse(HttpStatusCode.OK, """{ "results": [] }""");
        });

        var results = await connector.SearchAsync("query", 25);

        Assert.Multiple(() =>
        {
            Assert.That(captured?.RequestUri?.Query, Is.EqualTo("?q=query&num=10"));
            Assert.That(results, Is.Empty);
        });
    }

    [Test]
    public void SearchAsync_WhenUnauthorized_ThrowsWithoutLeakingTheKey()
    {
        using var connector = CreateConnector(_ => JsonResponse(HttpStatusCode.Unauthorized, """{ "detail": "Invalid API key" }"""));

        var exception = Assert.ThrowsAsync<HttpRequestException>(() => connector.SearchAsync("query", 3));

        Assert.Multiple(() =>
        {
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(exception.Message, Does.Contain("Invalid API key"));
            Assert.That(exception.Message, Does.Not.Contain("test-key"));
        });
    }

    private static SerplyConnector CreateConnector(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new("test-key", new HttpClient(new DelegateHandler(handler)), EndPoint);

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
