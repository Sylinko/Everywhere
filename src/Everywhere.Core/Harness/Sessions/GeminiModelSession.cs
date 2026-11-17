using System.Globalization;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Harness.Providers.Gemini;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// <see cref="ModelSession"/> implementation for Google Gemini models via the stateless Interactions v1 API.
/// </summary>
public sealed class GeminiModelSession : ModelSession
{
    /// <inheritdoc />
    public override IChatClient ChatClient { get; }

    /// <inheritdoc />
    public override string Dialect => GeminiInteractionsClient.DialectName;

    /// <inheritdoc />
    public override bool SupportsToolResultBlocks => true;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiModelSession"/> class.
    /// </summary>
    public GeminiModelSession(
        AssistantConfiguration configuration,
        GoogleOptions options,
        ModelConnection connection) : base(configuration, connection)
    {
        if (configuration.ModelId is not { Length: > 0 } model)
        {
            throw new ArgumentException("Missing Gemini model.", nameof(configuration));
        }

        var endpoint = ModelProviderSchemaExtensions.GetGeminiInteractionsUri(new Uri(connection.Endpoint));
        ChatClient = new GeminiInteractionsClient(
            connection.HttpClient,
            endpoint,
            model,
            connection.ApiKey)
        {
            ThinkingLevel = options.ResolveReasoningEffort(configuration),
            ThinkingBudget = int.TryParse(options.ThinkingBudget, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) ? budget : null,
            IncludeThoughts = options.IncludeThoughts,
            Temperature = float.TryParse(options.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : null,
            TopP = float.TryParse(options.TopP, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP) ? topP : null,
            TopK = int.TryParse(options.TopK, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topK) ? topK : null
        };
    }

    /// <inheritdoc />
    public override bool HasToolCallUpdate(ChatResponseUpdate update) =>
        base.HasToolCallUpdate(update) ||
        update.RawRepresentation is JsonElement { ValueKind: JsonValueKind.Object } step &&
        step.TryGetProperty("type", out var type) &&
        type.GetString() == "function_call";
}