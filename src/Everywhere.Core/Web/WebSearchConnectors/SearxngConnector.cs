using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
///     SearXNG Search API
/// </summary>
/// <param name="httpClient"></param>
/// <param name="uri"></param>
public sealed partial class SearxngConnector(HttpClient httpClient, Uri uri)
    : WebSearchClient<SearxngConnector.Response>(httpClient, new Range(0, 50))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => SearxngJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(
            HttpMethod.Get,
            new UriBuilder(uri)
            {
                Query = $"q={Uri.EscapeDataString(query)}&format=json"
            }.Uri
        );
    }

    [JsonSerializable(typeof(Response))]
    private partial class SearxngJsonSerializerContext : JsonSerializerContext;

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("results")]
        public IReadOnlyList<Result>? Results { get; init; }

        public IEnumerable<TextSearchResult> ToResults() => Results?.Select(x => new TextSearchResult(x.Content)
        {
            Name = x.PublishedDate1?.ToString("G") ?? x.PublishedDate2?.ToString("G") switch
            {
                { } date => $"{x.Title} (Published: {date})",
                _ => x.Title,
            },
            Link = x.Url,
        }) ?? [];
    }

    public sealed class Result
    {
        /// <summary>
        ///     The title of the search result.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        ///     The URL of the search result.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        ///     The full content of the search result (if available).
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        ///     The publication date of the search result.
        /// </summary>
        [JsonPropertyName("publishedDate")]
        public DateTime? PublishedDate1 { get; set; }

        [JsonPropertyName("pubDate")]
        public DateTime? PublishedDate2 { get; set; }
    }
}