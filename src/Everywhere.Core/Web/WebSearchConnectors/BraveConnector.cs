using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public sealed partial class BraveConnector(string apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<BraveConnector.Response>(httpClient, new Range(0, 20))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => BraveJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "X-Subscription-Token", apiKey }
            },
            Content = JsonContent.Create(new Request(query, count), BraveJsonSerializerContext.Default.Request)
        };
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class BraveJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("q")] string Query,
        [property: JsonPropertyName("count")] int Count
    );

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("error")]
        public Error? Error { get; init; }

        [JsonPropertyName("web")]
        public Web? Web { get; init; }

        public IEnumerable<TextSearchResult> ToResults()
        {
            if (Error is not null)
            {
                throw new HttpRequestException(
                    $"Brave Web Search API returned error (Id: {Error.Id}, Status: {Error.Status}, Code: {Error.Code}): {Error.Detail}");
            }

            return Web?.SearchResults?.Select(x => new TextSearchResult(x.Description ?? "")
            {
                Name = x.Title,
                Link = x.Url,
            }) ?? [];
        }
    }

    public sealed class Error
    {
        /// <summary>
        ///     A unique identifier for this particular occurrence of the problem.
        /// </summary>
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        /// <summary>
        ///     The HTTP status code applicable to this problem, expressed as a string value.
        /// </summary>
        [JsonPropertyName("status")]
        public required int Status { get; init; }

        /// <summary>
        ///     An application-specific error code, expressed as a string value.
        /// </summary>
        [JsonPropertyName("code")]
        public required string Code { get; init; }

        /// <summary>
        ///     The error message returned by the Brave Search API.
        /// </summary>
        [JsonPropertyName("detail")]
        public string? Detail { get; init; } = string.Empty;
    }

    public sealed class Web
    {
        [JsonPropertyName("results")]
        public IReadOnlyList<WebSearchResult>? SearchResults { get; init; }
    }

    public sealed class WebSearchResult
    {
        /// <summary>
        ///     The title of the search result.
        /// </summary>
        [JsonPropertyName("title")]
        public required string Title { get; init; }

        /// <summary>
        ///     The URL of the search result.
        /// </summary>
        [JsonPropertyName("url")]
        public required string Url { get; init; }

        /// <summary>
        ///     The description/snippet of the search result.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}