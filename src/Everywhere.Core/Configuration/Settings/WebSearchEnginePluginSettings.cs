using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Views;
using Everywhere.Web;

namespace Everywhere.Configuration;

[TypeConverter(typeof(FallbackEnumConverter))]
public enum WebSearchEngineProviderId
{
    Official,
    AnySearch,
    Bocha,
    Brave,
    Google,
    Jina,
    SearXNG,
    Serply,
    Tavily,
    UniFuncs,
}

public interface IWebSearchEngineProvider
{
    WebSearchEngineProviderId Id { get; }

    IDynamicLocaleKey HeaderKey { get; }

    string IconUrl { get; }

    string? DocumentsUrl { get; }

    SettingsItems SettingsItems { get; }

    IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory);
}

[GeneratedSettingsItems]
public sealed partial class OfficialWebSearchEngineSettings : ObservableObject
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OfficialWebSearchEngineProvider_Depth_Header,
        LocaleKey.OfficialWebSearchEngineProvider_Depth_Description)]
    [SettingsItem(Group = "_")]
    public partial OfficialConnector.SearchDepth Depth { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OfficialWebSearchEngineProvider_Topic_Header,
        LocaleKey.OfficialWebSearchEngineProvider_Topic_Description)]
    [SettingsItem(Group = "_")]
    public partial OfficialConnector.SearchTopic Topic { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.OfficialWebSearchEngineProvider_TimeRange_Header,
        LocaleKey.OfficialWebSearchEngineProvider_TimeRange_Description)]
    [SettingsItem(Group = "_")]
    public partial OfficialConnector.SearchTimeRange TimeRange { get; set; }
}

[GeneratedSettingsItems]
public sealed partial class OfficialWebSearchEngineProvider : ObservableObject, IWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public WebSearchEngineProviderId Id => WebSearchEngineProviderId.Official;

    [JsonIgnore]
    [SettingsItemIgnore]
    public IDynamicLocaleKey HeaderKey { get; } = new DynamicLocaleKey(LocaleKey.WebSearchEngineProvider_Official);

    [JsonIgnore]
    [SettingsItemIgnore]
    public string IconUrl => "avares://Everywhere.Core/Assets/Icons/everywhere-rounded.png";

    [JsonIgnore]
    [SettingsItemIgnore]
    public string? DocumentsUrl => null;

    [SettingsItemIgnore]
    public OfficialWebSearchEngineSettings Settings { get; } = new();

    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["Ghost", "NoHeading"])]
    public SettingsControl<OfficialWebSearchProviderSettingsControl> SettingsControl =>
        new(x => new OfficialWebSearchProviderSettingsControl(x, Settings));

    public bool Validate() => true;

    public IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new OfficialConnector(httpClientFactory.CreateClient(nameof(ICloudClient)), Settings);

    public override bool Equals(object? obj) => obj is IWebSearchEngineProvider provider && Id == provider.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

public abstract class ThirdPartyWebSearchEngineProvider : ObservableValidator, IWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract WebSearchEngineProviderId Id { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract IDynamicLocaleKey HeaderKey { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract string IconUrl { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract string? DocumentsUrl { get; }

    public abstract SettingsItems SettingsItems { get; }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    public abstract IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory);

    protected static Uri EnsureUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
        {
            throw new HandledException(
                new ArgumentException(
                    $"The configured web-search endpoint '{url}' is not a valid absolute HTTP or HTTPS URL. Ask the user to correct it in Settings > Web Search."),
                LocaleKey.BuiltInChatPlugin_Web_InvalidWebSearchEngineEndpoint_ErrorMessage);
        }

        // Extract only the base URI without query parameters
        return new UriBuilder(uri) { Query = string.Empty }.Uri;
    }

    public override bool Equals(object? obj) => obj is IWebSearchEngineProvider provider && Id == provider.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

[GeneratedSettingsItems]
public abstract partial class OptionalApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId id,
    IDynamicLocaleKey headerKey,
    string iconUrl,
    string? docsUrl,
    string defaultEndPoint,
    ObservableCollection<ApiKey> apiKeys
) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id { get; } = id;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = headerKey;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl { get; } = iconUrl;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? DocumentsUrl { get; } = docsUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualEndPoint))]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    [SettingsItem(Group = "_", Modifier = nameof(ApplyEndPointDefaultValueItem))]
    public partial string? EndPoint { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public string ActualEndPoint => string.IsNullOrEmpty(EndPoint) ? defaultEndPoint : EndPoint;

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Guid ApiKey { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header_Optional,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(apiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (OptionalApiKeyWebSearchEngineProvider x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });

    private SettingsDefaultValueItem ApplyEndPointDefaultValueItem(SettingsStringItem item)
    {
        item.PlaceholderText = defaultEndPoint;
        return new SettingsDefaultValueItem(item)
        {
            ResetCommand = new RelayCommand(() => EndPoint = null)
        };
    }
}

public sealed class AnySearchWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : OptionalApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.AnySearch,
    new DirectLocaleKey("AnySearch"),
    "avares://Everywhere.Core/Assets/Icons/anysearch-color.png",
    "https://www.anysearch.com",
    "https://api.anysearch.com/v1/search",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory)
    {
        return new AnySearchConnector(
            Configuration.ApiKey.GetKey(ApiKey), // can be null
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
    }
}

[GeneratedSettingsItems]
public abstract partial class ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId id,
    IDynamicLocaleKey headerKey,
    string iconUrl,
    string? docsUrl,
    string defaultEndPoint,
    ObservableCollection<ApiKey> apiKeys
) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id { get; } = id;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = headerKey;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl { get; } = iconUrl;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? DocumentsUrl { get; } = docsUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualEndPoint))]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    [SettingsItem(Group = "_", Modifier = nameof(ApplyEndPointDefaultValueItem))]
    public partial string? EndPoint { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    protected string ActualEndPoint => string.IsNullOrEmpty(EndPoint) ? defaultEndPoint : EndPoint;

    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ApiKey), nameof(Configuration.ApiKey.Validate))]
    public partial Guid ApiKey { get; set; }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(apiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (ApiKeyWebSearchEngineProvider x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });

    protected SettingsDefaultValueItem ApplyEndPointDefaultValueItem(SettingsStringItem item)
    {
        item.PlaceholderText = defaultEndPoint;
        return new SettingsDefaultValueItem(item)
        {
            ResetCommand = new RelayCommand(() => EndPoint = null)
        };
    }

    protected static string EnsureApiKey(Guid id) =>
        Configuration.ApiKey.GetKey(id) ??
        throw new HandledException(
            new UnauthorizedAccessException(
                "The API key required by the configured web-search provider is missing. Ask the user to configure it in Settings > Web Search."),
            LocaleKey.BuiltInChatPlugin_Web_WebSearchEngineApiKeyNotSet_ErrorMessage);
}

public sealed class BochaWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.Bocha,
    new DynamicLocaleKey(LocaleKey.WebSearchEngineProvider_Bocha),
    "avares://Everywhere.Core/Assets/Icons/bocha-color.png",
    "https://open.bochaai.com",
    "https://api.bocha.cn/v1/web-search",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new BochaConnector(
            EnsureApiKey(ApiKey),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

public sealed class BraveWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.Brave,
    new DirectLocaleKey("Brave"),
    "avares://Everywhere.Core/Assets/Icons/brave-color.png",
    "https://brave.com/search/api",
    "https://api.search.brave.com/res/v1/web/search",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new BraveConnector(
            EnsureApiKey(ApiKey),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

[GeneratedSettingsItems]
public sealed partial class GoogleWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.Google,
    new DirectLocaleKey("Google"),
    "avares://Everywhere.Core/Assets/Icons/google-color.svg",
    "https://developers.google.com/custom-search/v1/overview",
    "https://customsearch.googleapis.com",
    apiKeys)
{
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Header,
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Description)]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GoogleWebSearchEngineProvider), nameof(ValidateSearchEngineId))]
    [SettingsItem(Group = "_")]
    public partial string? SearchEngineId { get; set; }

    public static ValidationResult? ValidateSearchEngineId(string? searchEngineId)
    {
        if (string.IsNullOrWhiteSpace(searchEngineId))
        {
            return new ValidationResult(LocaleKey.ValidationErrorMessage_Required.I18N());
        }

        return ValidationResult.Success;
    }

    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new GoogleConnector(
            EnsureApiKey(ApiKey),
            SearchEngineId ??
            throw new HandledException(
                new UnauthorizedAccessException(
                    "Google web search requires a Search Engine ID. Ask the user to configure it in Settings > Web Search."),
                LocaleKey.BuiltInChatPlugin_Web_GoogleSearchEngineIdNotSet_ErrorMessage),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

public sealed class JinaWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.Jina,
    new DirectLocaleKey("Jina"),
    "avares://Everywhere.Core/Assets/Icons/jina-light.svg",
    "https://jina.ai",
    "https://s.jina.ai",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new JinaConnector(
            EnsureApiKey(ApiKey),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

[GeneratedSettingsItems]
public sealed partial class SearXNGWebSearchEngineProvider : ThirdPartyWebSearchEngineProvider
{
    private const string DefaultEndPoint = "https://searxng.example.com/search";

    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id => WebSearchEngineProviderId.SearXNG;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("SearXNG");

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl => "avares://Everywhere.Core/Assets/Icons/searxng-color.svg";

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string DocumentsUrl => "https://docs.searxng.org";

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    [DefaultValue(DefaultEndPoint)]
    public partial string? EndPoint { get; set; }

    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new SearxngConnector(
            httpClientFactory.CreateClient(),
            EnsureUri(string.IsNullOrEmpty(EndPoint) ? DefaultEndPoint : EndPoint));
}

public sealed class SerplyWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.Serply,
    new DirectLocaleKey("Serply"),
    "avares://Everywhere.Core/Assets/Icons/serply-color.svg",
    "https://serply.io/docs",
    "https://api.serply.io/v1/search",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new SerplyConnector(
            EnsureApiKey(ApiKey),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

public sealed class TavilyWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.Tavily,
    new DirectLocaleKey("Tavily"),
    "avares://Everywhere.Core/Assets/Icons/tavily-color.svg",
    "https://tavily.com",
    "https://api.tavily.com/search",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new TavilyConnector(
            EnsureApiKey(ApiKey),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

public sealed class UniFuncsWebSearchEngineProvider(
    ObservableCollection<ApiKey> apiKeys
) : ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId.UniFuncs,
    new DirectLocaleKey("UniFuncs"),
    "avares://Everywhere.Core/Assets/Icons/unifuncs-color.png",
    "https://www.unifuncs.com",
    "https://api.unifuncs.com/api/web-search/search",
    apiKeys)
{
    public override IWebSearchEngineConnector CreateConnector(IHttpClientFactory httpClientFactory) =>
        new UniFuncsConnector(
            EnsureApiKey(ApiKey),
            httpClientFactory.CreateClient(),
            EnsureUri(ActualEndPoint));
}

[GeneratedSettingsItems]
public sealed partial class WebSearchEngineSettings : ObservableObject
{
    [SettingsItemIgnore]
    public ObservableImmutableDictionary<WebSearchEngineProviderId, IWebSearchEngineProvider> Providers { get; }

    [SettingsItemIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    public partial WebSearchEngineProviderId SelectedProviderId { get; set; } = WebSearchEngineProviderId.AnySearch;

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.WebSearchEngineSettings_SelectedProvider_Header,
        LocaleKey.WebSearchEngineSettings_SelectedProvider_Description)]
    [SettingsItem(
        Group = LocaleKey.BuiltInChatPlugin_Web_WebSearch_Header,
        DocumentUrlBindingPath = $"{nameof(SelectedProvider)}.{nameof(IWebSearchEngineProvider.DocumentsUrl)}")]
    [SettingsSelectionItem($"{nameof(Providers)}.Values", DataTemplateKey = typeof(IWebSearchEngineProvider))]
    [SettingsItems(IsExpanded = true)]
    public IWebSearchEngineProvider? SelectedProvider
    {
        get => Providers.GetValueOrDefault(SelectedProviderId);
        set
        {
            if (Equals(SelectedProviderId, value?.Id)) return;
            SelectedProviderId = value?.Id ?? default;
        }
    }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ObservableCollection<ApiKey> ApiKeys { get; set; }

    public WebSearchEngineSettings()
    {
        ApiKeys = [];
        Providers = new ObservableImmutableDictionary<WebSearchEngineProviderId, IWebSearchEngineProvider>(
        [
            MakeKeyValuePair(new OfficialWebSearchEngineProvider()),
            MakeKeyValuePair(new AnySearchWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new BochaWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new BraveWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new GoogleWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new JinaWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new SearXNGWebSearchEngineProvider()),
            MakeKeyValuePair(new SerplyWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new TavilyWebSearchEngineProvider(ApiKeys)),
            MakeKeyValuePair(new UniFuncsWebSearchEngineProvider(ApiKeys)),
        ]);

        static KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider> MakeKeyValuePair(IWebSearchEngineProvider provider)
        {
            return new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(provider.Id, provider);
        }
    }
}