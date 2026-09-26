using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace Everywhere.AI;

/// <summary>
/// Executes one conditional HTTP fetch for a model catalog. Catalog-specific clients own request
/// construction, response validation, deserialization, and error semantics.
/// </summary>
public abstract class ModelCatalogHttpClient<TCatalog>(IHttpClientFactory httpClientFactory, string httpClientName = "") where TCatalog : class
{
    private const int StreamBufferSize = 81920;

    protected virtual long? MaximumResponseBytes => null;

    protected virtual void ConfigureHttpClient(HttpClient client) { }

    protected abstract HttpRequestMessage CreateRequest();

    protected abstract TCatalog ReadResponse(HttpResponseMessage response, ReadOnlySpan<byte> content);

    public async Task<ModelCatalogFetchResult<TCatalog>> FetchAsync(string? entityTag = null, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(httpClientName);
        ConfigureHttpClient(client);

        using var request = CreateRequest();
        if (EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag))
        {
            request.Headers.IfNoneMatch.Add(parsedEntityTag);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new ModelCatalogFetchResult<TCatalog>.NotModified();
        }

        var content = await ReadContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var catalog = ReadResponse(response, content);
        return new ModelCatalogFetchResult<TCatalog>.Modified(catalog, response.Headers.ETag?.ToString());
    }

    private async Task<byte[]> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var maximumResponseBytes = MaximumResponseBytes;
        if (maximumResponseBytes is { } maximum && content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException("Model catalog exceeds the size limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (maximumResponseBytes is { } limit && destination.Length + read > limit)
                {
                    throw new InvalidDataException("Model catalog exceeds the size limit.");
                }

                destination.Write(buffer, 0, read);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public abstract record ModelCatalogFetchResult<TCatalog> where TCatalog : class
{
    private ModelCatalogFetchResult() { }

    public sealed record Modified(TCatalog Catalog, string? EntityTag) : ModelCatalogFetchResult<TCatalog>;

    public sealed record NotModified : ModelCatalogFetchResult<TCatalog>;
}

public class ModelCatalogHttpException(
    HttpStatusCode statusCode,
    DateTimeOffset? retryAfter = null,
    string? message = null
) : HttpRequestException(message ?? $"Model catalog returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode)
{
    public DateTimeOffset? RetryAfter { get; } = retryAfter;
}