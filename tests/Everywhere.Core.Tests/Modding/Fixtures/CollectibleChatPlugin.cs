using System.Text.Json.Serialization;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;

namespace Everywhere.Core.Tests.Modding.Fixtures;

/// <summary>
/// Loaded from a second, collectible copy of the test assembly while keeping host contracts shared.
/// Exercises both source-generated and default reflection tool bindings alongside localized keys.
/// </summary>
public sealed class CollectibleChatPlugin : ModChatPlugin
{
    /// <inheritdoc />
    public override bool IsDefaultEnabled => true;

    /// <inheritdoc />
    public override IDynamicLocaleKey HeaderKey { get; }

    /// <inheritdoc />
    public override IDynamicLocaleKey DescriptionKey { get; }

    /// <summary>
    /// Registers source-generated and reflection-bound methods inside the collectible context.
    /// </summary>
    public CollectibleChatPlugin() : base("collectible_search")
    {
        IDynamicLocaleKey[] collectibleParts = [new DirectLocaleKey("Collectible"), new DirectLocaleKey("search")];
        HeaderKey = new AggregateDynamicLocaleKey(collectibleParts, " ");
        DescriptionKey = new FormattedDynamicLocaleKey("{0} with nested options", new DirectLocaleKey("Search"), new DirectLocaleKey("extra"));

        _functionsSource.Add(
            new NativeChatFunction(
                Search,
                ChatFunctionPermissions.FileRead,
                beforeCallHook: _ => new ChatPluginDynamicLocaleKeyDisplayBlock(
                    new FormattedDynamicLocaleKey("{0}", new AggregateDynamicLocaleKey(collectibleParts, " "))),
                permissionConsentHook: (_, _) => IsDefaultEnabled ? null : false,
                jsonSerializerOptions: SearchJsonContext.Default.Options));
        _functionsSource.Add(
            new NativeChatFunction(
                QuickLookup,
                ChatFunctionPermissions.FileRead));
    }

    /// <summary>
    /// Exercises MEAI complex argument binding with plugin-owned generated JSON metadata.
    /// </summary>
    private string Search(SearchRequest request, int limit = 20)
    {
        return IsDefaultEnabled ? $"{request.Location.Directory}:{string.Join(',', request.Tags)}:{limit}" : string.Empty;
    }

    /// <summary>
    /// Exercises default MEAI reflection parameter binding without explicit serializer options.
    /// </summary>
    private static string QuickLookup(string query, int maxResults = 10) => $"{query}:{maxResults}";
}

/// <summary>
/// A plugin-owned complex parameter with nested and array values.
/// </summary>
public sealed class SearchRequest
{
    /// <summary>
    /// Gets or sets the search location.
    /// </summary>
    public SearchLocation Location { get; set; } = new();

    /// <summary>
    /// Gets or sets requested tags.
    /// </summary>
    public string[] Tags { get; set; } = [];
}

/// <summary>
/// A nested plugin-owned parameter type.
/// </summary>
public sealed class SearchLocation
{
    /// <summary>
    /// Gets or sets the directory to search.
    /// </summary>
    public string Directory { get; set; } = "";
}

/// <summary>
/// Keeps schema generation and binding metadata inside the collectible assembly.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
internal partial class SearchJsonContext : JsonSerializerContext;
