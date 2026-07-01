using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for Ollama models.
/// </summary>
public sealed class OllamaKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OllamaApiClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaKernelMixin"/> class.
    /// </summary>
    public OllamaKernelMixin(
        Assistant assistant,
        ModelConnection connection
    ) : base(assistant, connection)
    {
        connection.HttpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);
        _client = new OllamaApiClient(connection.HttpClient, ModelId);
        ChatCompletionService = _client.AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
    }
}