using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public sealed partial class JinaConnector(string apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<JinaConnector.Response>(httpClient, new Range(0, 50))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => JinaJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "Authorization", $"Bearer {apiKey}" }
            },
            Content = JsonContent.Create(new Request(query, count), JinaJsonSerializerContext.Default.Request)
        };
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class JinaJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("q")] string Query,
        [property: JsonPropertyName("count")] int Count
    );

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("status")]
        public int? Status { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("data")]
        public IReadOnlyList<Item>? Data { get; init; }

        public IEnumerable<TextSearchResult> ToResults() => Data?.Select(x => new TextSearchResult(x.Description)
        {
            Name = x.Title,
            Link = x.Url,
        }) ?? [];
    }

    public sealed class Item
    {
        /// <summary>
        ///     The title of the search result.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        ///     The description/snippet of the search result.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        ///     The URL of the search result.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}