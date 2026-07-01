namespace Everywhere.AI;

public interface IModelDefinition
{
    /// <summary>
    /// Unique identifier for the model definition.
    /// This also serves as the model ID used in API requests.
    /// </summary>
    string? ModelId { get; }

    /// <summary>
    /// Whether the model supports function/tool calling.
    /// </summary>
    bool SupportsToolCall { get; }

    /// <summary>
    /// Modalities supported by the model for input.
    /// </summary>
    Modalities InputModalities { get; }

    /// <summary>
    /// Modalities supported by the model for output. This is used to determine the type of content the model can generate.
    /// </summary>
    Modalities OutputModalities { get; }

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// </summary>
    int ContextLimit { get; }

    /// <summary>
    /// Maximum number of tokens that the model can generate in a single response.
    /// </summary>
    int OutputLimit { get; }

    /// <summary>
    /// Special capabilities or optimizations that the model may have for specific tasks, such as generating titles or compressing context.
    /// </summary>
    ModelSpecializations Specializations { get; }

    /// <summary>
    /// The date when the model is expected to be deprecated. This is used to evaluate model availability and show warnings to users if their selected model is nearing deprecation or already deprecated.
    /// </summary>
    DateOnly? DeprecationDate { get; }
}