using Everywhere.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Everywhere.Harness;

/// <summary>
/// <see cref="ModelSession"/> implementation for Ollama models.
/// </summary>
public sealed class OllamaModelSession : ModelSession
{
    public const string DialectName = "ollama";

    /// <inheritdoc />
    public override IChatClient ChatClient => _client;

    /// <inheritdoc />
    public override string Dialect => DialectName;

    private readonly OllamaApiClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaModelSession"/> class.
    /// </summary>
    public OllamaModelSession(AssistantConfiguration configuration, ModelConnection connection) : base(configuration, connection)
    {
        connection.HttpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);
        _client = new OllamaApiClient(connection.HttpClient, Configuration.ModelId ?? string.Empty);
    }

    /// <inheritdoc />
    public override void ExtractExceptionEvidence(ChatExceptionEvidence evidence)
    {
        ChatExceptionEvidenceExtractor.ExtractOllama(evidence);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _client.Dispose();
    }
}
