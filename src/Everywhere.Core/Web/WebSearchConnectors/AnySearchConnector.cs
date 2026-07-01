using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public sealed partial class AnySearchConnector(string? apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<AnySearchConnector.Response>(httpClient, new Range(0, 100))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => AnySearchJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Headers =
            {
                { "Accept", "application/json" },
            },
            Content = JsonContent.Create(new Request(query, count), AnySearchJsonSerializerContext.Default.Request)
        };

        if (!apiKey.IsNullOrWhiteSpace())
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return requestMessage;
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class AnySearchJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults
    );

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("data")]
        public Data? Data { get; init; }

        public IEnumerable<TextSearchResult> ToResults()
        {
            if (Data is null)
            {
                throw new InvalidDataException($"AnySearch Web Search API returned an empty result. Message: {Message}");
            }

            return Data?.Results?.Select(x => new TextSearchResult(x.Content ?? "")
            {
                Name = x.Title,
                Link = x.Url,
            }) ?? [];
        }
    }

    public sealed class Data
    {
        [JsonPropertyName("results")]
        public IReadOnlyList<Result>? Results { get; init; }
    }

    public sealed class Result
    {
        /// <summary>
        ///     Result title
        /// </summary>
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        /// <summary>
        ///     Original source URL
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        /// <summary>
        ///     Short summary
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        /// <summary>
        ///     Cleaned-up body content
        /// </summary>
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }
}