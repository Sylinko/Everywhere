using System.Globalization;
using Everywhere.AI;
using Everywhere.Harness.Providers.Gemini;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// <see cref="ModelSession"/> implementation for Google Gemini models via the <c>generateContent</c> API.
/// </summary>
public sealed class GoogleModelSession : ModelSession
{
    /// <inheritdoc />
    public override IChatClient ChatClient { get; }

    /// <inheritdoc />
    public override string Dialect => GeminiGenerateContentClient.DialectName;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleModelSession"/> class.
    /// </summary>
    public GoogleModelSession(
        AssistantConfiguration configuration,
        GoogleOptions options,
        ModelConnection connection
    ) : base(configuration, connection)
    {
        if (configuration.ModelId is not { Length: > 0 } model)
        {
            throw new ArgumentException("Missing Gemini model.", nameof(configuration));
        }

        ChatClient = new GeminiGenerateContentClient(
            connection.HttpClient,
            new Uri(connection.Endpoint, UriKind.Absolute),
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
}
