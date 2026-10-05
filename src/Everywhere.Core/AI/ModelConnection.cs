namespace Everywhere.AI;

/// <summary>
/// Represents the resolved connection parameters for a model provider.
/// This decouples the connection source (user configuration vs. system constants/OAuth)
/// from the runtime behavior (provider-specific SDK initialization).
/// </summary>
/// <param name="Schema">The resolved provider schema.</param>
/// <param name="Endpoint">The normalized endpoint URL, ready to use.</param>
/// <param name="ApiKey">
/// The resolved API key in plaintext.
/// <c>null</c> means no API key is needed (e.g., the HttpClient handler manages authentication).
/// </param>
/// <param name="HttpClient">The connection-owned HTTP client supplied to the SDK.</param>
/// <param name="ExceptionEvidenceEnricher">Optional connection-specific evidence extraction, independent of protocol identity.</param>
/// <param name="RequestMaxRetries">Maximum retries per logical request; -1 allows unlimited eligible retries.</param>
public readonly record struct ModelConnection(
    ModelProviderSchema Schema,
    string Endpoint,
    string? ApiKey,
    HttpClient HttpClient,
    Action<ChatExceptionEvidence>? ExceptionEvidenceEnricher,
    int RequestMaxRetries = 5
) : IDisposable
{
    public void Dispose() => HttpClient.Dispose();
}