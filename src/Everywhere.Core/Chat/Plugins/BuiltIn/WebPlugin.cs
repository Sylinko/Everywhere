using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Web;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ZLinq;

namespace Everywhere.Chat.Plugins.BuiltIn;

public sealed partial class WebPlugin : BuiltInChatPlugin
{
    public override IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_Header);
    public override IDynamicLocaleKey DescriptionKey { get; } = new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_Description);
    public override LucideIconKind? Icon => LucideIconKind.Globe;
    public override IReadOnlyList<SettingsItem> SettingsItems => _webBrowserSettings.SettingsItems;

    private readonly WebSearchEngineSettings _webSearchEngineSettings;
    private readonly WebBrowserSettings _webBrowserSettings;
    private readonly IWebBrowserHost _webBrowserHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebPlugin> _logger;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = WebPluginJsonSerializerContext.Default
    };

    public WebPlugin(
        Settings settings,
        IWebBrowserHost webBrowserHost,
        IHttpClientFactory httpClientFactory,
        ILogger<WebPlugin> logger) : base("web")
    {
        _webSearchEngineSettings = settings.Plugin.WebSearchEngine;
        _webBrowserSettings = settings.Plugin.WebBrowser;
        _webBrowserHost = webBrowserHost;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _functionsSource.Edit(list =>
        {
            list.Add(
                new BuiltInChatFunction(
                    SearchAsync,
                    ChatFunctionPermissions.NetworkAccess,
                    isVisible: false,
                    isEnabled: false,
                    onPermissionConsent: _ => true)); // always allow
            list.Add(
                new BuiltInChatFunction(
                    ExtractAsync,
                    ChatFunctionPermissions.NetworkAccess));
        });
    }

    private IWebSearchEngineConnector CreateConnector()
    {
        if (_webSearchEngineSettings.SelectedProvider is not { } provider)
        {
            throw new HandledException(
                new ArgumentException("Web search engine provider is not selected."),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_NoWebSearchEngineProviderSelected_ErrorMessage),
                showDetails: false);
        }

        return provider switch
        {
            OfficialWebSearchEngineProvider official => new OfficialConnector(
                _httpClientFactory.CreateClient(nameof(ICloudClient)),
                official.Settings),
            OptionalApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.AnySearch } anySearch =>
                new AnySearchConnector(
                    apiKey: anySearch.ApiKey != Guid.Empty ? EnsureApiKey(anySearch.ApiKey) : null,
                    _httpClientFactory.CreateClient(),
                    EnsureUri(anySearch.ActualEndPoint)),
            // ReSharper disable once IdentifierTypo
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Bocha } bocha =>
                new BoChaConnector(EnsureApiKey(bocha.ApiKey), _httpClientFactory.CreateClient(), EnsureUri(bocha.ActualEndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Brave } brave =>
                new BraveConnector(EnsureApiKey(brave.ApiKey), _httpClientFactory.CreateClient(), EnsureUri(brave.ActualEndPoint)),
            GoogleWebSearchEngineProvider google => new GoogleConnector(
                EnsureApiKey(google.ApiKey),
                google.SearchEngineId ??
                throw new HandledException(
                    new UnauthorizedAccessException("Search Engine ID is not set."),
                    new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_GoogleSearchEngineIdNotSet_ErrorMessage),
                    showDetails: false),
                _httpClientFactory.CreateClient(),
                EnsureUri(google.ActualEndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Jina } jina =>
                new JinaConnector(EnsureApiKey(jina.ApiKey), _httpClientFactory.CreateClient(), EnsureUri(jina.ActualEndPoint)),
            // ReSharper disable once InconsistentNaming
            SearXNGWebSearchEngineProvider searXNG =>
                new SearxngConnector(_httpClientFactory.CreateClient(), EnsureUri(searXNG.ActualEndPoint)),
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.Tavily } tavily =>
                new TavilyConnector(EnsureApiKey(tavily.ApiKey), _httpClientFactory.CreateClient(), EnsureUri(tavily.ActualEndPoint)),
            // ReSharper disable once IdentifierTypo
            ApiKeyWebSearchEngineProvider { Id: WebSearchEngineProviderId.UniFuncs } uniFuncs =>
                new UniFuncsConnector(EnsureApiKey(uniFuncs.ApiKey), _httpClientFactory.CreateClient(), EnsureUri(uniFuncs.ActualEndPoint)),
            _ => throw new HandledException(
                new NotSupportedException($"Web search engine provider '{provider.Id}' is not supported."),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_UnsupportedWebSearchEngineProvider_ErrorMessage),
                showDetails: false)
        };

        Uri EnsureUri(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
            {
                throw new HandledException(
                    new ArgumentException(
                        "Endpoint is not a valid absolute http/https URI. Please instruct the user to correct in Main Window > Web Search."),
                    new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_InvalidWebSearchEngineEndpoint_ErrorMessage),
                    showDetails: false);
            }

            // Extract only the base URI without query parameters
            return new UriBuilder(uri) { Query = string.Empty }.Uri;
        }

        string EnsureApiKey(Guid id) =>
            ApiKey.GetKey(id) ??
            throw new HandledException(
                new UnauthorizedAccessException("API key is not set. Please instruct the user to configure in Main Window > Web Search."),
                new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_WebSearchEngineApiKeyNotSet_ErrorMessage),
                showDetails: false);
    }

    /// <summary>
    /// Performs a web search using the provided query, count, and offset.
    /// </summary>
    /// <param name="displaySink"></param>
    /// <param name="query">The text to search for.</param>
    /// <param name="count">The number of results to return. Default is 10.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The value of the TResult parameter contains the search results as a string.</returns>
    /// <remarks>
    /// This method is marked as "unsafe." The usage of JavaScriptEncoder.UnsafeRelaxedJsonEscaping may introduce security risks.
    /// Only use this method if you are aware of the potential risks and have validated the input to prevent security vulnerabilities.
    /// </remarks>
    [KernelFunction("web_search")]
    [Description(
        "Searches the public web for real-time information. Returns a JSON array of web pages. " +
        "STRICTLY confined to internet content; DO NOT use to search local files or personal data. " +
        "Results may be inaccurate.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_WebSearch_Header, LocaleKey.BuiltInChatPlugin_Web_WebSearch_Description)]
    private async Task<string> SearchAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("Search query")] string query,
        [Description("Number of results. Default is 10.")] int count = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Performing web search with query: {Query}, count: {Count}", query, count);
        using var connector = CreateConnector();

        displaySink.AppendDynamicLocaleKey(
            new FormattedDynamicLocaleKey(
                LocaleKey.BuiltInChatPlugin_Web_WebSearch_Searching,
                new DirectLocaleKey(query)));

        var results = await connector.SearchAsync(query, count, cancellationToken).ConfigureAwait(false);
        var indexedResults = results
            .AsValueEnumerable()
            .Select((r, i) => new IndexedWebPage(
                Index: i + 1,
                Name: r.Name,
                Url: r.Link,
                Snippet: r.Value))
            .ToList();
        displaySink.AppendUrls(
            indexedResults.Select(r => new ChatPluginUrl(
                r.Url,
                new DirectLocaleKey((r.Name ?? r.Snippet).SafeSubstring(0, 64)))
            {
                Index = r.Index
            }).ToList());

        return JsonSerializer.Serialize(indexedResults, _jsonSerializerOptions);
    }

    [KernelFunction("web_extract")]
    [Description("Fetch and extract the main content from a web page. This tool is useful for summarizing or analyzing the content of a webpage.")]
    [DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Web_WebExtract_Header, LocaleKey.BuiltInChatPlugin_Web_WebExtract_Description)]
    private async Task<string> ExtractAsync(
        [FromKernelServices] IChatPluginDisplaySink displaySink,
        [Description("An array of URLs to fetch content from. Maximum 10.")] IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default)
    {
        switch (urls.Count)
        {
            case 0:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentError,
                    nameof(urls),
                    new ArgumentException("At least one URL must be provided."));
            }
            case > 10:
            {
                throw new HandledFunctionInvokingException(
                    HandledFunctionInvokingExceptionType.ArgumentError,
                    nameof(urls),
                    new ArgumentException("A maximum of 10 URLs can be processed at once."));
            }
        }

        var extractions = await Task.WhenAll(
            urls.DistinctBy(u => u.Trim()).Select(async url =>
            {
                displaySink.AppendDynamicLocaleKey(
                    new FormattedDynamicLocaleKey(
                        LocaleKey.BuiltInChatPlugin_Web_WebExtract_Visiting,
                        new DirectLocaleKey(url)));

                try
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                        uri.Scheme is not "http" and not "https")
                    {
                        throw new HandledFunctionInvokingException(
                            HandledFunctionInvokingExceptionType.ArgumentError,
                            nameof(urls),
                            new ArgumentException("Invalid URL format. Only absolute http/https URLs are allowed."));
                    }

                    var content = await _webBrowserHost.ExtractAsync(url, cancellationToken);
                    // ReSharper disable once RedundantCast
                    return (url, content, error: (string?)null);
                }
                catch (Exception ex)
                {
                    ex = HandledFunctionInvokingException.Handle(ex);
                    _logger.LogError(ex, "Failed to extract content from URL: {Url}", url);
                    // ReSharper disable once RedundantCast
                    return (url, content: (string?)null, error: ex.Message);
                }
            }));

        // Dynamic proportional token budget allocation per URL
        const int totalBudget = 40000;
        const int minPerUrl = 500;
        var desiredTokens = extractions.AsValueEnumerable().Select(e => e.content != null ? TokenHelper.EstimateTokenCount(e.content) : 0).ToList();
        var allocations = TokenBudget.Allocate(desiredTokens.AsSpan(), totalBudget, minTokensPerItem: minPerUrl);

        // Build output with trimmed content
        var resultBuilder = new StringBuilder();
        for (var i = 0; i < extractions.Length; i++)
        {
            var (url, content, error) = extractions[i];

            resultBuilder.Append("# Content from ").AppendLine(url).AppendLine();

            if (error != null)
            {
                resultBuilder.AppendLine("# Failed to extract:").AppendLine(error);
            }
            else if (content != null)
            {
                if (allocations[i] < desiredTokens[i])
                {
                    TokenHelper.OmitTo(content, resultBuilder, allocations[i]);
                }
                else
                {
                    resultBuilder.Append(content);
                }
            }

            resultBuilder.AppendLine().AppendLine("------").AppendLine();
        }

        return resultBuilder.ToString();
    }

    [JsonSerializable(typeof(List<IndexedWebPage>))]
    private partial class WebPluginJsonSerializerContext : JsonSerializerContext;

    private sealed record IndexedWebPage(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("snippet")] string Snippet
    );
}