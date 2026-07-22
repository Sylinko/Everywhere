using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.SemanticKernel.Data;

namespace Everywhere.Web;

public sealed partial class OfficialConnector(
    HttpClient httpClient,
    OfficialWebSearchEngineSettings settings
) : WebSearchClient<OfficialConnector.Response>(httpClient, new Range(0, 20))
{
    private readonly JsonSerializerOptions _requestJsonSerializerOptions = new()
    {
        TypeInfoResolver = OfficialJsonSerializerContext.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters =
        {
            new JsonStringEnumConverter<SearchDepth>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<SearchTopic>(JsonNamingPolicy.KebabCaseLower),
            new JsonStringEnumConverter<SearchTimeRange>(JsonNamingPolicy.KebabCaseLower),
        }
    };

    protected override JsonTypeInfo<Response> JsonTypeInfo => OfficialJsonSerializerContext.Default.Response;

    protected override HttpRequestMessage CreateSearchRequest(string query, int count)
    {
        return new HttpRequestMessage(HttpMethod.Post, CloudConstants.WebSearchBaseUrl)
        {
            Content = JsonContent.Create(
                new Request(
                    query,
                    count,
                    settings.Depth.EnsureDefined(),
                    settings.Topic.EnsureDefined(),
                    settings.TimeRange.EnsureDefined()),
                options: _requestJsonSerializerOptions)
        };
    }

    protected override Exception? TransformSearchException(Exception exception)
    {
        if (exception is UserNotLoginException)
        {
            throw new HandledException(
                new UserNotLoginException(
                    "The configured official web-search provider requires the user to sign in. Ask the user to sign in or configure a third-party search provider."),
                new DynamicLocaleKey(LocaleKey.HandledSystemException_UserNotLogin),
                showDetails: false);
        }

        return null;
    }

    [TypeConverter(typeof(FallbackEnumConverter))]
    public enum SearchDepth
    {
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchDepth_Basic)]
        Basic,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchDepth_Fast)]
        Fast,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchDepth_UltraFast)]
        UltraFast
    }

    [TypeConverter(typeof(FallbackEnumConverter))]
    public enum SearchTopic
    {
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTopic_General)]
        General,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTopic_News)]
        News,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTopic_Finance)]
        Finance
    }

    [TypeConverter(typeof(FallbackEnumConverter))]
    public enum SearchTimeRange
    {
        [DynamicLocaleKey(LocaleKey.Common_Default)]
        Default,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTimeRange_Day)]
        Day,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTimeRange_Month)]
        Week,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTimeRange_Month)]
        Month,
        [DynamicLocaleKey(LocaleKey.OfficialConnector_SearchTimeRange_Year)]
        Year
    }

    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Response))]
    private partial class OfficialJsonSerializerContext : JsonSerializerContext;

    private sealed record Request(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults,
        [property: JsonPropertyName("search_depth")] SearchDepth Depth = SearchDepth.Basic,
        [property: JsonPropertyName("topic")] SearchTopic Topic = SearchTopic.General,
        [property: JsonPropertyName("time_range")] SearchTimeRange TimeRange = SearchTimeRange.Default,
        [property: JsonPropertyName("include_domains")] IReadOnlyList<string>? IncludeDomains = null,
        [property: JsonPropertyName("exclude_domains")] IReadOnlyList<string>? ExcludeDomains = null
    );

    public sealed class Response : ApiPayload<IReadOnlyList<WebSearchResult>>, IWebSearchResponse
    {
        public IEnumerable<TextSearchResult> ToResults()
        {
            return EnsureData().Select(x => new TextSearchResult(x.Content ?? "")
            {
                Name = x.Title,
                Link = x.Url,
            });
        }
    }

    public sealed class ErrorResult
    {
        [JsonPropertyName("error")]
        public required string Error { get; init; }
    }

    public sealed class WebSearchResult
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