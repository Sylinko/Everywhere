using System.Text;
using Everywhere.AI;

namespace Everywhere.Harness.Providers;

/// <summary>
/// Sends HTTP requests for custom <see cref="Microsoft.Extensions.AI.IChatClient"/> adapters while
/// capturing bounded error bodies and standard retry/request-ID headers on failure.
/// </summary>
internal static class HttpErrorEvidence
{
    private static readonly string[] RetainedHeaders = ["Retry-After", "Retry-After-Ms", "request-id", "x-request-id"];

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string providerDisplayName,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
            var buffer = new char[ChatExceptionEvidenceExtractor.MaximumBodyLength];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            var error = new HttpRequestException(
                $"{providerDisplayName} returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
            error.Data["Everywhere.Http.StatusCode"] = (int)response.StatusCode;
            error.Data["Everywhere.Http.ResponseBody"] = new string(buffer, 0, count);
            foreach (var header in RetainedHeaders)
            {
                if (response.Headers.TryGetValues(header, out var values))
                {
                    error.Data["Everywhere.Http." + header] = string.Join(", ", values);
                }
            }

            throw error;
        }
    }
}
