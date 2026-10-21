using System.Text.Json.Serialization;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;

namespace Everywhere.Core.Tests.Modding.Fixtures;

/// <summary>
/// Loaded from a second, collectible copy of the test assembly. All host contracts remain shared.
/// Generated serialization avoids unrelated process-wide reflection caches in this lifetime test.
/// </summary>
public sealed class CollectibleChatPlugin : ModChatPlugin
{
    /// <inheritdoc />
    public override bool IsDefaultEnabled => true;

    /// <inheritdoc />
    public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Collectible search");

    /// <inheritdoc />
    public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Search with nested options");

    /// <summary>
    /// Registers a real SK method with plugin-owned parameter types and generated JSON metadata.
    /// </summary>
    public CollectibleChatPlugin() : base("collectible_search")
    {
        _functionsSource.Add(new NativeChatFunction(
            Search,
            ChatFunctionPermissions.FileRead,
            permissionConsentHook: (_, _) => IsDefaultEnabled ? null : false,
            beforeCallHook: _ => new ChatPluginDynamicLocaleKeyDisplayBlock(HeaderKey),
            jsonSerializerOptions: SearchJsonContext.Default.Options));
    }

    /// <summary>
    /// Exercises SK's complex argument binding and optional parameter handling.
    /// </summary>
    private string Search(SearchRequest request, int limit = 20)
    {
        return $"{request.Location.Directory}:{string.Join(',', request.Tags)}:{limit}";
    }
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
