using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public sealed partial class TavilyConnector(string apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<TavilyConnector.Response>(httpClient, new Range(0, 20))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => TavilyJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "Authorization", $"Bearer {apiKey}" }
            },
            Content = JsonContent.Create(new Request(query, count), TavilyJsonSerializerContext.Default.Request)
        };
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class TavilyJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults
    );

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("detail")]
        public ErrorDetail? Detail { get; init; }

        [JsonPropertyName("results")]
        public IReadOnlyList<Result>? Results { get; init; }

        public IEnumerable<TextSearchResult> ToResults()
        {
            if (Detail is not null)
            {
                throw new InvalidDataException($"Tavily Web Search API returned an empty result. Error: {Detail?.Error}");
            }

            return Results?.Select(x => new TextSearchResult(x.Content ?? "")
            {
                Name = x.Title,
                Link = x.Url,
            }) ?? [];
        }
    }

    public sealed class ErrorDetail
    {
        [JsonPropertyName("error")]
        public required string Error { get; init; }
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
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        /// <summary>
        ///     The description/snippet of the search result.
        /// </summary>
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}