using System.Net;
using System.Text.Json;

namespace Everywhere.AI;

internal sealed class ModelsDevModelCatalogClient(
    IHttpClientFactory httpClientFactory,
    long maximumResponseBytes
) : ModelCatalogHttpClient<Dictionary<string, ModelsDevProvider>>(httpClientFactory)
{
    protected override long? MaximumResponseBytes => maximumResponseBytes;

    protected override void ConfigureHttpClient(HttpClient client) => client.Timeout = TimeSpan.FromSeconds(30);

    protected override HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, "https://models.dev/api.json");

    protected override Dictionary<string, ModelsDevProvider> ReadResponse(HttpResponseMessage response, ReadOnlySpan<byte> content)
    {
        if (!response.IsSuccessStatusCode)
        {
            DateTimeOffset? retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests ?
                response.Headers.RetryAfter?.Date ??
                DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1)) :
                null;
            throw new ModelCatalogHttpException(
                response.StatusCode,
                retryAfter,
                $"models.dev returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return DeserializeSupportedProviders(content);
    }

    private static Dictionary<string, ModelsDevProvider> DeserializeSupportedProviders(ReadOnlySpan<byte> json)
    {
        var supported = PresetModelTemplates.Providers.AsValueEnumerable().Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal);
        var providers = new Dictionary<string, ModelsDevProvider>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("Expected a provider map.");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidDataException("Invalid provider map.");
            }

            var id = reader.GetString();
            if (!reader.Read())
            {
                throw new InvalidDataException("Unexpected end of provider map.");
            }

            if (id is not null && supported.Contains(id))
            {
                var provider = JsonSerializer.Deserialize(ref reader, ModelsDevJsonContext.Default.ModelsDevProvider);
                if (provider is not null) providers.Add(id, provider);
            }
            else
            {
                reader.Skip();
            }
        }

        return providers;
    }
}