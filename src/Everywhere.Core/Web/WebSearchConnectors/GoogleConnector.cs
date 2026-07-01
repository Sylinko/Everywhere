// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Web;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

/// <summary>
///     Google Custom Search API
///     https://developers.google.com/custom-search/v1/using_rest
/// </summary>
/// <param name="apiKey">Google Custom Search API key</param>
/// <param name="searchEngineId">Google Search Engine ID (looks like "a12b345...")</param>
/// <param name="httpClient">The HTTP client to use for requests.</param>
/// <param name="uri">The URI of the Google Custom Search API. Defaults to https://customsearch.googleapis.com</param>
public sealed partial class GoogleConnector(string apiKey, string searchEngineId, HttpClient httpClient, Uri uri)
    : WebSearchClient<GoogleConnector.Response>(httpClient, new Range(1, 10))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => GoogleJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(
            HttpMethod.Get,
            new UriBuilder(uri)
            {
                Path = "/customsearch/v1",
                Query = $"key={HttpUtility.UrlEncode(apiKey)}" +
                    $"&cx={HttpUtility.UrlEncode(searchEngineId)}" +
                    $"&q={HttpUtility.UrlEncode(query)}" +
                    $"&num={count}",
            }.Uri
        );
    }

    [JsonSerializable(typeof(Response))]
    private sealed partial class GoogleJsonSerializerContext : JsonSerializerContext;

    public sealed class Response : IWebSearchResponse
    {
        [JsonPropertyName("items")]
        public IReadOnlyList<Item>? Items { get; set; }

        [JsonPropertyName("searchInformation")]
        public SearchInformation? SearchInformation { get; set; }

        public IEnumerable<TextSearchResult> ToResults() => Items?.Select(item => new TextSearchResult(item.Snippet ?? string.Empty)
        {
            Name = item.Title,
            Link = item.Link,
        }) ?? [];
    }

    public sealed class Item
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }

        [JsonPropertyName("displayLink")]
        public string? DisplayLink { get; set; }
    }

    public sealed class SearchInformation
    {
        [JsonPropertyName("totalResults")]
        public string? TotalResults { get; set; }

        [JsonPropertyName("searchTime")]
        public double SearchTime { get; set; }
    }
}