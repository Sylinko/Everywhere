using System.Globalization;
using Everywhere.Harness.Providers.Mistral;
using Microsoft.Extensions.AI;

namespace Everywhere.AI;

/// <summary>
/// <see cref="ModelSession"/> implementation for Mistral AI models via native <see cref="MistralChatClient"/>.
/// </summary>
public sealed class MistralModelSession : ModelSession
{
    /// <inheritdoc />
    public override IChatClient ChatClient { get; }

    /// <inheritdoc />
    public override string Dialect => MistralChatClient.DialectName;

    /// <summary>
    /// Initializes a new instance of the <see cref="MistralModelSession"/> class.
    /// </summary>
    public MistralModelSession(
        AssistantConfiguration configuration,
        MistralOptions options,
        ModelConnection connection
    ) : base(configuration, connection)
    {
        var baseEndpoint = new Uri(Endpoint.EndsWith('/') ? Endpoint : Endpoint + "/", UriKind.Absolute);
        var completionsUri = new Uri(baseEndpoint, "chat/completions");

        ChatClient = new MistralChatClient(
            connection.HttpClient,
            completionsUri,
            Configuration.ModelId ?? string.Empty,
            ApiKey)
        {
            Temperature = float.TryParse(options.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : 0.7f,
            TopP = float.TryParse(options.TopP, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP) ? topP : 1f,
            ReasoningEffort = options.IncludeReasoningContent ? options.ResolveReasoningEffort(Configuration) : "none"
        };
    }

    /// <inheritdoc />
    public override bool HasToolCallUpdate(ChatResponseUpdate update) =>
        base.HasToolCallUpdate(update) ||
        update.RawRepresentation is MistralChatClient.ToolDeltaMarker;
}