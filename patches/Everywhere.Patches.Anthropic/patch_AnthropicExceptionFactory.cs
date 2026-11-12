using System.Net;
using Anthropic.Core;
using Anthropic.Exceptions;
using MonoMod;

namespace Everywhere.Patches.Anthropic;

/// <summary>
/// Retains retry hints and request identifiers on the SDK's original exception subtype.
/// </summary>
/// <remarks>
/// Anthropic 13.8 passes the response to its exception factory. Wrap that boundary instead
/// of copying the request loop, so retries, authentication, tracing, and response ownership
/// remain entirely in the SDK. Read headers before its caller disposes the response.
/// See <see href="https://github.com/anthropics/anthropic-sdk-csharp/blob/2a7781f8f18c2afb630ac255a2a7a62566dd1351/src/Anthropic/Exceptions/AnthropicExceptionFactory.cs">the reviewed upstream factory</see>.
/// </remarks>
[MonoModPatch("Anthropic.Exceptions.AnthropicExceptionFactory")]
internal class patch_AnthropicExceptionFactory
{
    // Do not use MonoModReplace: preserve the complete upstream exception factory.
    internal static extern AnthropicApiException orig_CreateApiException(HttpStatusCode statusCode, string responseBody, HttpResponse? response);

    internal static AnthropicApiException CreateApiException(HttpStatusCode statusCode, string responseBody, HttpResponse? response)
    {
        var failure = orig_CreateApiException(statusCode, responseBody, response);
        if (response is null)
        {
            return failure;
        }

        foreach (var name in new[] { "Retry-After", "Retry-After-Ms", "request-id", "x-request-id" })
        {
            if (response.TryGetHeaderValues(name, out var values))
            {
                failure.Data["Everywhere.Http." + name] = string.Join(",", values);
            }
        }

        return failure;
    }
}