using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public sealed partial class UniFuncsConnector(string apiKey, HttpClient httpClient, Uri uri)
    : WebSearchClient<UniFuncsConnector.Response>(httpClient, new Range(1, 50))
{
    protected override JsonTypeInfo<Response> JsonTypeInfo => UniFuncsJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Headers =
            {
                { "Accept", "application/json" },
                { "Authorization", $"Bearer {apiKey}" }
            },
            Content = JsonContent.Create(new Request(query, count), UniFuncsJsonSerializerContext.Default.Request)
        };
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class UniFuncsJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("count")] int Count
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
            if (Code != 0)
            {
                throw new HttpRequestException($"UniFuncs API returned error ({Code}): {Message}");
            }

            return Data?.WebPages?.Select(x => new TextSearchResult(x.Summary ?? x.Snippet)
            {
                Name = x.Name,
                Link = x.Url,
            }) ?? [];
        }
    }

    public sealed class Data
    {
        [JsonPropertyName("webPages")]
        public IReadOnlyList<WebPage>? WebPages { get; init; }
    }

    public sealed class WebPage
    {
        /// <summary>
        ///     The name/title of the web page.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        ///     The snippet of the web page.
        /// </summary>
        [JsonPropertyName("snippet")]
        public string Snippet { get; set; } = string.Empty;

        /// <summary>
        ///     The URL of the web page.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        ///     The summary of the web page.
        /// </summary>
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }
}