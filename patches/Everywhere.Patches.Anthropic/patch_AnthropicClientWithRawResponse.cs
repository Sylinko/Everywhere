// Copyright Anthropic, PBC. Licensed under MIT; see LICENSE.txt.
// Complete Execute<T> implementation copied from Anthropic 12.45.0, commit
// 13c68f0e37cbbf1623cf7daa13d3074286cf7ffd. Everywhere changes are marked below.

using System.Net;
using Anthropic.Core;
using Anthropic.Credentials;
using Anthropic.Exceptions;
using MonoMod;

namespace Everywhere.Patches.Anthropic;

/// <summary>
/// Retains retry hints and request identifiers that Anthropic's API exception factory
/// otherwise discards when converting a failed HTTP response into an SDK exception.
/// </summary>
/// <remarks>
/// The complete upstream request loop is retained so SDK retry, credential refresh,
/// exception subtypes, and response ownership keep their existing behavior. Only the
/// terminal exception gains selected response headers in its own Data dictionary;
/// there is no shared response cache or application retry policy in this patch.
/// See <see href="https://github.com/anthropics/anthropic-sdk-csharp/blob/13c68f0e37cbbf1623cf7daa13d3074286cf7ffd/src/Anthropic/AnthropicClient.cs">the pinned upstream implementation</see>.
/// </remarks>
[MonoModPatch("Anthropic.AnthropicClientWithRawResponse")]
internal class patch_AnthropicClientWithRawResponse
{
    [MonoModIgnore]
    public extern int? MaxRetries { get; }

    [MonoModIgnore]
    private extern bool UsingTokenCredentials { get; }

    [MonoModIgnore]
    private readonly TokenCache? _tokenCache;

    [MonoModReplace]
    public async Task<HttpResponse> Execute<T>(
        HttpRequest<T> request,
        CancellationToken cancellationToken = default
    ) where T : ParamsBase
    {
        var maxRetries = MaxRetries ?? ClientOptions.DefaultMaxRetries;
        var retries = 0;
        var hasAuthRetryConsumed = false;
        while (true)
        {
            var response = (HttpResponse?)null;
            try
            {
                response = await ExecuteOnce(request, retries, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (++retries > maxRetries || !ShouldRetry(e))
                {
                    throw;
                }
            }

            // 401 with token credentials: force-refresh the token and retry once.
            // Gated on retries == 0 so an auth retry never stacks on top of a transport
            // retry. Body replayability is not gated separately — ExecuteOnce rebuilds the
            // body from request.Params on every attempt, the same as the transport-retry path.
            if (response?.StatusCode == HttpStatusCode.Unauthorized && UsingTokenCredentials)
            {
                // UsingTokenCredentials => _tokenCache != null, so the ! deref is safe.
                if (!hasAuthRetryConsumed && retries == 0)
                {
                    hasAuthRetryConsumed = true;
                    var failedToken = _tokenCache!.Cached?.Token;
                    // Prime the cache so the retry's BeforeSend picks up the fresh token.
                    var fresh = default(AccessToken);
                    try
                    {
                        fresh = await _tokenCache
                            .GetTokenAsync(forceRefresh: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // A failed refresh abandons the 401 response, so release it here.
                        response.Dispose();
                        throw;
                    }
                    // Only retry if the refresh actually produced a different token —
                    // StaticTokenCredentials / ANTHROPIC_AUTH_TOKEN can't change, so
                    // skipping avoids one wasted round-trip.
                    if (!string.Equals(fresh.Token, failedToken, StringComparison.Ordinal))
                    {
                        response.Dispose();
                        retries++;
                        continue;
                    }
                }
                else
                {
                    // Not retrying — invalidate so the caller's next request fetches fresh.
                    _tokenCache!.Invalidate();
                }
            }

            if (response != null && (++retries > maxRetries || !ShouldRetry(response)))
            {
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                try
                {
                    // Everywhere: retain selected headers on the original exception subtype,
                    // before the SDK disposes this response. Retry/auth behavior is unchanged.
                    var failure = AnthropicExceptionFactory.CreateApiException(
                        response.StatusCode,
                        await response.ReadAsString(cancellationToken).ConfigureAwait(false)
                    );
                    foreach (var name in new[] { "Retry-After", "Retry-After-Ms", "request-id", "x-request-id" })
                    {
                        if (response.TryGetHeaderValues(name, out var values))
                            failure.Data["Everywhere.Http." + name] = string.Join(",", values);
                    }
                    throw failure;
                }
                catch (HttpRequestException e)
                {
                    throw new AnthropicIOException("I/O Exception", e);
                }
                finally
                {
                    response.Dispose();
                }
            }

            var backoff = default(TimeSpan);
            try
            {
                backoff = ComputeRetryBackoff(retries, response);
            }
            finally
            {
                // A malformed Retry-After header makes the computation throw; the response
                // being retried is abandoned either way.
                response?.Dispose();
            }
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    [MonoModIgnore]
    private extern Task<HttpResponse> ExecuteOnce<T>(HttpRequest<T> request, int retryCount, CancellationToken cancellationToken) where T : ParamsBase;

    [MonoModIgnore]
    private static extern bool ShouldRetry(Exception exception);

    [MonoModIgnore]
    private static extern bool ShouldRetry(HttpResponse response);

    [MonoModIgnore]
    private static extern TimeSpan ComputeRetryBackoff(int retries, HttpResponse? response);
}