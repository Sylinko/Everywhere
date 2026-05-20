using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;
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
    Tavily,
    UniFuncs,
}

public interface IWebSearchEngineProvider
{
    WebSearchEngineProviderId Id { get; }

    IDynamicResourceKey HeaderKey { get; }

    string IconUrl { get; }

    string? DocsUrl { get; }

    SettingsItems SettingsItems { get; }

    bool Validate();
}

[GeneratedSettingsItems]
public sealed partial class OfficialWebSearchEngineSettings : ObservableObject
{
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OfficialWebSearchEngineProvider_Depth_Header,
        LocaleKey.OfficialWebSearchEngineProvider_Depth_Description)]
    public partial OfficialConnector.SearchDepth Depth { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OfficialWebSearchEngineProvider_Topic_Header,
        LocaleKey.OfficialWebSearchEngineProvider_Topic_Description)]
    public partial OfficialConnector.SearchTopic Topic { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.OfficialWebSearchEngineProvider_TimeRange_Header,
        LocaleKey.OfficialWebSearchEngineProvider_TimeRange_Description)]
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
    public IDynamicResourceKey HeaderKey { get; } = new DynamicResourceKey(LocaleKey.WebSearchEngineProvider_Official);

    [JsonIgnore]
    [SettingsItemIgnore]
    public string IconUrl => "avares://Everywhere.Core/Assets/Icons/everywhere-rounded.png";

    [JsonIgnore]
    [SettingsItemIgnore]
    public string? DocsUrl => null;

    [SettingsItemIgnore]
    public OfficialWebSearchEngineSettings Settings { get; } = new();

    [DynamicResourceKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["Ghost", "NoHeading"])]
    public SettingsControl<OfficialWebSearchProviderSettingsControl> SettingsControl =>
        new(x => new OfficialWebSearchProviderSettingsControl(x, Settings));

    public bool Validate() => true;

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
    public abstract IDynamicResourceKey HeaderKey { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract string IconUrl { get; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public abstract string? DocsUrl { get; }

    public abstract SettingsItems SettingsItems { get; }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    public override bool Equals(object? obj) => obj is IWebSearchEngineProvider provider && Id == provider.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

[GeneratedSettingsItems]
public sealed partial class GoogleWebSearchEngineProvider(ObservableCollection<ApiKey> apiKeys) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id => WebSearchEngineProviderId.Google;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicResourceKey HeaderKey { get; } = new DirectResourceKey("Google");

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl => "avares://Everywhere.Core/Assets/Icons/google-color.svg";

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string DocsUrl => "https://developers.google.com/custom-search/v1/overview";

    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    public Customizable<string> EndPoint { get; } = new("https://customsearch.googleapis.com", isDefaultValueReadonly: true);

    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ApiKey), nameof(Configuration.ApiKey.Validate))]
    public partial Guid ApiKey { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(apiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Header,
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Description)]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(GoogleWebSearchEngineProvider), nameof(ValidateSearchEngineId))]
    public partial string? SearchEngineId { get; set; }

    public static ValidationResult? ValidateSearchEngineId(string? searchEngineId)
    {
        if (string.IsNullOrWhiteSpace(searchEngineId))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        return ValidationResult.Success;
    }
}

[GeneratedSettingsItems]
public sealed partial class ApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId id,
    IDynamicResourceKey headerKey,
    string iconUrl,
    string? docsUrl,
    ObservableCollection<ApiKey> apiKeys
) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id { get; } = id;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicResourceKey HeaderKey { get; } = headerKey;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl { get; } = iconUrl;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? DocsUrl { get; } = docsUrl;

    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    public required Customizable<string> EndPoint { get; init; }

    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(ApiKey), nameof(Configuration.ApiKey.Validate))]
    public partial Guid ApiKey { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(apiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
        });
}

[GeneratedSettingsItems]
public sealed partial class OptionalApiKeyWebSearchEngineProvider(
    WebSearchEngineProviderId id,
    IDynamicResourceKey headerKey,
    string iconUrl,
    string? docsUrl,
    ObservableCollection<ApiKey> apiKeys
) : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id { get; } = id;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicResourceKey HeaderKey { get; } = headerKey;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl { get; } = iconUrl;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string? DocsUrl { get; } = docsUrl;

    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    public required Customizable<string> EndPoint { get; init; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Guid ApiKey { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_ApiKey_Header_Optional,
        LocaleKey.WebSearchEngineProvider_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(apiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
        });
}

[GeneratedSettingsItems]
public sealed partial class SearXNGWebSearchEngineProvider : ThirdPartyWebSearchEngineProvider
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public override WebSearchEngineProviderId Id => WebSearchEngineProviderId.SearXNG;

    [JsonIgnore]
    [SettingsItemIgnore]
    public override IDynamicResourceKey HeaderKey { get; } = new DirectResourceKey("SearXNG");

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string IconUrl => "avares://Everywhere.Core/Assets/Icons/searxng-color.svg";

    [JsonIgnore]
    [SettingsItemIgnore]
    public override string DocsUrl => "https://docs.searxng.org";

    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    public Customizable<string> EndPoint { get; } = new("https://searxng.example.com/search", isDefaultValueReadonly: true);
}

[GeneratedSettingsItems]
public sealed partial class WebSearchEngineSettings : ObservableObject
{
    [SettingsItemIgnore]
    public ObservableImmutableDictionary<WebSearchEngineProviderId, IWebSearchEngineProvider> Providers { get; }

    [SettingsItemIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    public partial WebSearchEngineProviderId SelectedProviderId { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
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
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Official,
                new OfficialWebSearchEngineProvider()),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.AnySearch,
                new OptionalApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.AnySearch,
                    new DirectResourceKey("AnySearch"),
                    "avares://Everywhere.Core/Assets/Icons/anysearch-color.png",
                    "https://www.anysearch.com",
                    ApiKeys)
                {
                    EndPoint = new Customizable<string>("https://api.anysearch.com/v1/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Bocha,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Bocha,
                    new DynamicResourceKey(LocaleKey.WebSearchEngineProvider_Bocha),
                    "avares://Everywhere.Core/Assets/Icons/bocha-color.png",
                    "https://open.bochaai.com",
                    ApiKeys)
                {
                    EndPoint = new Customizable<string>("https://api.bocha.cn/v1/web-search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Brave,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Brave,
                    new DirectResourceKey("Brave"),
                    "avares://Everywhere.Core/Assets/Icons/brave-color.png",
                    "https://brave.com/search/api",
                    ApiKeys)
                {
                    EndPoint = new Customizable<string>("https://api.search.brave.com/res/v1/web/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Google,
                new GoogleWebSearchEngineProvider(ApiKeys)),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Jina,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Jina,
                    new DirectResourceKey("Jina"),
                    "avares://Everywhere.Core/Assets/Icons/jina-light.svg",
                    "https://jina.ai",
                    ApiKeys)
                {
                    EndPoint = new Customizable<string>("https://s.jina.ai", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.SearXNG,
                new SearXNGWebSearchEngineProvider()),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.Tavily,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.Tavily,
                    new DirectResourceKey("Tavily"),
                    "avares://Everywhere.Core/Assets/Icons/tavily-color.svg",
                    "https://tavily.com",
                    ApiKeys)
                {
                    EndPoint = new Customizable<string>("https://api.tavily.com/search", isDefaultValueReadonly: true)
                }),
            new KeyValuePair<WebSearchEngineProviderId, IWebSearchEngineProvider>(
                WebSearchEngineProviderId.UniFuncs,
                new ApiKeyWebSearchEngineProvider(
                    WebSearchEngineProviderId.UniFuncs,
                    new DirectResourceKey("UniFuncs"),
                    "avares://Everywhere.Core/Assets/Icons/unifuncs-color.png",
                    "https://www.unifuncs.com",
                    ApiKeys)
                {
                    EndPoint = new Customizable<string>("https://api.unifuncs.com/api/web-search/search", isDefaultValueReadonly: true)
                }),
        ]);
    }
}