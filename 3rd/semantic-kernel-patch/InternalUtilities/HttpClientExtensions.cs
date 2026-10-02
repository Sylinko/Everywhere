// Copyright (c) Microsoft. All rights reserved.
// Adapted from Semantic Kernel's shared HTTP helper for Everywhere's .NET target.

using System.Diagnostics.CodeAnalysis;

namespace Microsoft.SemanticKernel.Http;

[ExcludeFromCodeCoverage]
internal static class HttpClientExtensions
{
    /// <summary>
    /// Returns successful responses to the caller, which owns their disposal. Transport
    /// and cancellation exceptions retain their original type and status provenance.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendWithSuccessCheckAsync(this HttpClient client, HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;

        // A failed response never reaches the caller's using statement.
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException exception)
            {
                var failure = new HttpOperationException(response.StatusCode, body, exception.Message, exception);
                foreach (var name in new[] { "Retry-After", "Retry-After-Ms", "request-id", "x-request-id" })
                {
                    if (response.Headers.TryGetValues(name, out var values))
                        failure.Data["Everywhere.Http." + name] = string.Join(",", values);
                }
                throw failure;
            }
        }

        throw new InvalidOperationException("A non-success response passed EnsureSuccessStatusCode.");
    }

    /// <summary>Checks a buffered response using the same error ownership rules.</summary>
    internal static Task<HttpResponseMessage> SendWithSuccessCheckAsync(this HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken) =>
        client.SendWithSuccessCheckAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
}
