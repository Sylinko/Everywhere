using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
///     Web search connector for the Serply Google SERP API (https://serply.io/docs).
///     Serply returns at most 10 results per request, so the count is clamped to that range.
/// </summary>
public sealed partial class SerplyConnector(string apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<SerplyConnector.Response>(httpClient, new Range(0, 10))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => SerplyJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        var requestUri = new UriBuilder(uri)
        {
            Query = $"q={Uri.EscapeDataString(query)}&num={count}"
        }.Uri;

        return new HttpRequestMessage(HttpMethod.Get, requestUri)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "X-Api-Key", apiKey }
            }
        };
    }

    [JsonSerializable(typeof(Response))]
    private partial class SerplyJsonSerializerContext : JsonSerializerContext;

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("results")]
        public IReadOnlyList<Result>? Results { get; init; }

        public IEnumerable<TextSearchResult> ToResults() => Results?
            .Where(x => !string.IsNullOrEmpty(x.Link))
            .Select(x => new TextSearchResult(x.Description ?? "")
            {
                Name = x.Title,
                Link = x.Link,
            }) ?? [];
    }

    public sealed class Result
    {
        /// <summary>
        ///     The title of the search result.
        /// </summary>
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        /// <summary>
        ///     The URL of the search result.
        /// </summary>
        [JsonPropertyName("link")]
        public string? Link { get; init; }

        /// <summary>
        ///     The description/snippet of the search result.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
