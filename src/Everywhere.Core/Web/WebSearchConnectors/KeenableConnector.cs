using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
///     Web search connector for the Keenable Search API (https://docs.keenable.ai/api-reference/search).
///     The API key is optional. With a key, requests go to the configured endpoint with an <c>X-API-Key</c> header.
///     Without one, they go to the keyless <c>/public</c> path under it, which is rate limited per IP address.
///     Both paths require an <c>X-Keenable-Title</c> header naming the application, and accept 1 to 50 results.
/// </summary>
public sealed partial class KeenableConnector(string? apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<KeenableConnector.Response>(httpClient, new Range(1, 50))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => KeenableJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        var hasApiKey = !apiKey.IsNullOrWhiteSpace();
        var requestUri = hasApiKey ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/public" }.Uri;
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "X-Keenable-Title", "Everywhere" }
            },
            Content = JsonContent.Create(new Request(query, count), KeenableJsonSerializerContext.Default.Request)
        };

        if (hasApiKey)
        {
            requestMessage.Headers.Add("X-API-Key", apiKey);
        }

        return requestMessage;
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class KeenableJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults
    );

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("results")]
        public IReadOnlyList<Result>? Results { get; init; }

        public IEnumerable<TextSearchResult> ToResults()
        {
            if (Results is null)
            {
                throw new InvalidDataException("Keenable Web Search API returned no results field.");
            }

            return Results
                .Where(x => !x.Url.IsNullOrWhiteSpace())
                .Select(x => new TextSearchResult(x.Snippet ?? x.Description ?? "")
                {
                    Name = x.Title,
                    Link = x.Url,
                });
        }
    }

    public sealed class Result
    {
        /// <summary>
        ///     The title of the page.
        /// </summary>
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        /// <summary>
        ///     The URL of the page.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        /// <summary>
        ///     The page's meta description, which can be empty.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        /// <summary>
        ///     The page text that matches the query.
        /// </summary>
        [JsonPropertyName("snippet")]
        public string? Snippet { get; init; }
    }
}
