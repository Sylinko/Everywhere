using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Chat.Plugins.Mcp;

public static class McpServiceExtension
{
    public const string McpClientName = "McpClient";

    /// <summary>
    /// Registers the HttpClient and handlers needed for MCP HTTP transports.
    /// </summary>
    public static IServiceCollection AddManagedMcp(this IServiceCollection services)
    {
        services.AddTransient<ContentLengthBufferingHandler>();
        services.AddTransient<McpSessionExpiryHandler>();

        services
            .AddHttpClient(
                McpClientName,
                client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new HttpClientHandler
                {
                    Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                    UseProxy = true,
                    AllowAutoRedirect = true,
                })
            .AddHttpMessageHandler<ContentLengthBufferingHandler>()
            .AddHttpMessageHandler<McpSessionExpiryHandler>();

        return services;
    }

    /// <summary>
    /// A delegating handler that buffers the request content to compute and set the
    /// Content-Length header. This is useful for servers that do not support
    /// chunked transfer encoding.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class ContentLengthBufferingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                // By calling LoadIntoBufferAsync, we force the content to be buffered in memory.
                // This allows the HttpContent instance to calculate its length, which then gets
                // automatically set as the Content-Length header when the request is sent.
                // This effectively disables chunked transfer encoding.
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A delegating handler that intercepts non-404 4xx responses from MCP servers
    /// and converts them to 404 if the response body indicates a session expired error.
    /// This allows the SDK's standard <c>SetSessionExpired</c> path to handle it.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class McpSessionExpiryHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Only intercept non-404 4xx responses.
            if (response.StatusCode is not HttpStatusCode.NotFound && (int)response.StatusCode is >= 400 and < 500)
            {
                // Buffer the response content so we can read it and still return it if no match.
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!ContainsSessionExpiredKeyword(body)) return response;

                var newContent = new StringContent(body);

                // Preserve original content headers (such as Content-Type/charset).
                // Skip Content-Length because it is computed from the replacement content.
                foreach (var header in response.Content.Headers)
                {
                    if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                var newResponse = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = response.RequestMessage,
                    ReasonPhrase = "Session Expired (rewritten by McpSessionExpiryHandler)",
                    Version = response.Version,
                    Content = newContent,
                };

                // Copy response headers.
                foreach (var header in response.Headers)
                {
                    newResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                // Preserve trailing headers as well.
                foreach (var header in response.TrailingHeaders)
                {
                    newResponse.TrailingHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Dispose();
                return newResponse;
            }

            return response;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ContainsSessionExpiredKeyword(string body) =>
            body.Contains("session", StringComparison.OrdinalIgnoreCase) &&
            (body.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("expires", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }
}