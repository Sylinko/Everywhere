namespace Everywhere.AI;

/// <summary>
/// Represents a provider template for customizing assistant.
/// This used for both online and local models.
/// </summary>
public sealed record ModelProviderTemplate
{
    /// <summary>
    /// Unique identifier for the model provider.
    /// This ID is used to distinguish between different providers.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the model provider, used for UI.
    /// This name is shown to the user in the application's settings or model selection UI.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// This icon is displayed next to the provider's name in the UI.
    /// </summary>
    public string? LightIconUrl { get; set; }

    /// <summary>
    /// This icon is displayed next to the provider's name in the UI.
    /// </summary>
    public string? DarkIconUrl { get; set; }

    /// <summary>
    /// Endpoint URL for the model provider's API.
    /// e.g., "https://api.example.com/v1/models".
    /// This URL is used to send requests to the model provider's servers.
    /// </summary>
    public required string Endpoint { get; set; }

    /// <summary>
    /// Official website URL for the model provider, if available.
    /// This URL is displayed to the user for more information about the provider.
    /// </summary>
    public string? OfficialWebsiteUrl { get; set; }

    /// <summary>
    /// Documentation URL for the model provider, if available.
    /// This usually points to the Everywhere's user guide or API documentation.
    /// This URL provides users with detailed information on how to use
    /// the model provider's features and API.
    /// </summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>
    /// Schema used by the model provider.
    /// This schema defines the structure of the data exchanged with the provider.
    /// </summary>
    public ModelProviderSchema Schema { get; set; }

    /// <summary>
    /// Timeout for each request to the model, in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// A list of model definitions provided by this model provider.
    /// Each model definition describes a specific model offered by the provider,
    /// including its capabilities and limitations.
    /// </summary>
    public required IReadOnlyList<ModelDefinitionTemplate> ModelDefinitions { get; set; }

    public bool Equals(ModelProviderTemplate? other) => Id == other?.Id;

    public override int GetHashCode() => Id.GetHashCode();
}