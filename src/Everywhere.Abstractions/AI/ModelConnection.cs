namespace Everywhere.AI;

/// <summary>
/// Represents the resolved connection parameters for a model provider.
/// This decouples the connection source (user configuration vs. system constants/OAuth)
/// from the runtime behavior (provider-specific SDK initialization).
/// </summary>
/// <param name="Schema">The resolved provider schema (never <see cref="ModelProviderSchema.Official"/>).</param>
/// <param name="Endpoint">The normalized endpoint URL, ready to use.</param>
/// <param name="ApiKey">
/// The resolved API key in plaintext.
/// <c>null</c> means no API key is needed (e.g., the HttpClient handler manages authentication).
/// </param>
public readonly record struct ModelConnection(
    ModelProviderSchema Schema,
    string Endpoint,
    string? ApiKey,
    HttpClient HttpClient,
    Func<Exception, Exception>? ChatExceptionTransformer);
