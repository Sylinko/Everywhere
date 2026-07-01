using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
///     BoCha Web Search API
///     https://bocha-ai.feishu.cn/wiki/RXEOw02rFiwzGSkd9mUcqoeAnNK
/// </summary>
public sealed partial class BoChaConnector(string apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<BoChaConnector.Response>(httpClient, new Range(1, 50))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => BoChaJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Headers =
            {
                { "Authorization", $"Bearer {apiKey}" }
            },
            Content = JsonContent.Create(new Request(query, count, true), BoChaJsonSerializerContext.Default.Request)
        };
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class BoChaJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("summary")] bool Summary
    );

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("msg")]
        public string? Message { get; init; }

        [JsonPropertyName("data")]
        public Data? Data { get; init; }

        public IEnumerable<TextSearchResult> ToResults() => Data?.WebPages?.Value?.Select(x => new TextSearchResult(x.Summary ?? x.Snippet)
        {
            Name = x.Name,
            Link = x.Url,
        }) ?? [];
    }

    public sealed class Data
    {
        [JsonPropertyName("webPages")]
        public WebPages? WebPages { get; set; }
    }

    public sealed class WebPages
    {
        /// <summary>
        ///     a nullable WebPage array object containing the Web Search API response data.
        /// </summary>
        [JsonPropertyName("value")]
        public IReadOnlyList<WebPage>? Value { get; set; }
    }

    public sealed class WebPage
    {
        /// <summary>
        ///     The name of the result.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        ///     The URL of the result.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        ///     The result snippet.
        /// </summary>
        [JsonPropertyName("snippet")]
        public string Snippet { get; set; } = string.Empty;

        /// <summary>
        ///     The result snippet.
        /// </summary>
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }
}